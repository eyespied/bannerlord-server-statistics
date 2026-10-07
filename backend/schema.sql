-- CIStatistics: immutable round ingestion. Apply as database owner.
begin;
create table if not exists public.ci_statistics_event_calendar(event_date date primary key, type text not null default 'battle');
alter table public.ci_statistics_event_calendar enable row level security;
revoke all on public.ci_statistics_event_calendar from public,anon,authenticated;
grant select,insert,update,delete on public.ci_statistics_event_calendar to service_role;
create table if not exists public.ci_statistics_servers (
 id uuid primary key default gen_random_uuid(), name text not null check(length(name) between 1 and 100),
 token_hash text not null unique check(token_hash ~ '^[0-9a-f]{64}$'),
 enabled boolean not null default true, scheduled_only boolean not null default false,
 created_at timestamptz not null default now(), last_received_at timestamptz,
 rate_window timestamptz not null default now(), rate_count integer not null default 0
);
create table if not exists public.ci_statistics_rounds (
 id uuid primary key default gen_random_uuid(), server_id uuid not null references public.ci_statistics_servers(id),
 match_id uuid not null, round_number integer not null check(round_number between 1 and 10000),
 event_date date not null, started_at timestamptz not null, ended_at timestamptz not null,
 map_id text not null, game_type text not null, report jsonb not null,
 received_at timestamptz not null default now(), unique(server_id,match_id,round_number)
);
create index if not exists ci_statistics_rounds_date on public.ci_statistics_rounds(event_date,started_at);
alter table public.ci_statistics_servers enable row level security;
alter table public.ci_statistics_rounds enable row level security;
revoke all on public.ci_statistics_servers, public.ci_statistics_rounds from public,anon,authenticated;
grant select,insert,update,delete on public.ci_statistics_servers to service_role;
revoke all on public.ci_statistics_rounds from service_role;
grant select on public.ci_statistics_rounds to service_role;

create or replace function public.ingest_ci_statistics(p_token_hash text,p_report jsonb)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare s public.ci_statistics_servers; existing jsonb; day date; started timestamptz; ended timestamptz;
begin
 select * into s from public.ci_statistics_servers where token_hash=p_token_hash and enabled for update;
 if not found then return jsonb_build_object('status','unauthorized'); end if;
 if jsonb_typeof(p_report) <> 'object' or (p_report->>'schema_version') <> '1'
    or jsonb_typeof(p_report->'players') <> 'array'
    or jsonb_array_length(p_report->'players') not between 1 and 600 then
  return jsonb_build_object('status','invalid'); end if;
 started := (p_report->>'started_at')::timestamptz;
 ended := (p_report->>'ended_at')::timestamptz;
 if started is null or ended is null or ended < started or ended > now()+interval '5 minutes'
    or ended-started > interval '24 hours' then return jsonb_build_object('status','invalid'); end if;
 day := (started at time zone 'Europe/London')::date;
 select report into existing from public.ci_statistics_rounds
  where server_id=s.id and match_id=(p_report->>'match_id')::uuid and round_number=(p_report->>'round_number')::integer;
 if found then
  if existing=p_report then return jsonb_build_object('status','duplicate','event_date',day); end if;
  return jsonb_build_object('status','conflict');
 end if;
 if now()-s.rate_window >= interval '1 minute' then s.rate_count:=0; s.rate_window:=now(); end if;
 if s.rate_count>=120 then return jsonb_build_object('status','rate_limited'); end if;
 update public.ci_statistics_servers set rate_window=s.rate_window,rate_count=s.rate_count+1,last_received_at=now() where id=s.id;
 if s.scheduled_only and not exists(select 1 from public.ci_statistics_event_calendar where event_date=day and type='battle') then
  return jsonb_build_object('status','ignored','reason','unscheduled_day','event_date',day);
 end if;
 insert into public.ci_statistics_rounds(server_id,match_id,round_number,event_date,started_at,ended_at,map_id,game_type,report)
 values(s.id,(p_report->>'match_id')::uuid,(p_report->>'round_number')::integer,day,started,ended,
 p_report->>'map_id',p_report->>'game_type',p_report);
 return jsonb_build_object('status','accepted','event_date',day);
end; $$;
revoke all on function public.ingest_ci_statistics(text,jsonb) from public,anon,authenticated;
grant execute on function public.ingest_ci_statistics(text,jsonb) to service_role;
create or replace view public.ci_statistics_event_days with(security_invoker=true) as
 select event_date,count(distinct r.id) as rounds,count(distinct p->>'p_steam_id') as players
 from public.ci_statistics_rounds r cross join lateral jsonb_array_elements(r.report->'players') p
 group by event_date;
revoke all on public.ci_statistics_event_days from public,anon,authenticated;
grant select on public.ci_statistics_event_days to service_role;
create or replace view public.ci_statistics_player_totals with(security_invoker=true) as
 select r.event_date,
 p->>'p_steam_id' as steam_id,
 (array_agg(p->>'p_display_name' order by r.started_at desc,r.id desc))[1] as display_name,
 count(*) as rounds_played,
 max(r.received_at) as updated_at,
 sum(coalesce((p->>'p_score')::bigint,0)) as score,
 sum(coalesce((p->>'p_kills')::bigint,0)) as kills,
 sum(coalesce((p->>'p_deaths')::bigint,0)) as deaths,
 sum(coalesce((p->>'p_assists')::bigint,0)) as assists,
 sum(coalesce((p->>'p_horse_damage')::bigint,0)) as horse_damage,
 sum(coalesce((p->>'p_horse_kills')::bigint,0)) as horse_kills,
 sum(coalesce((p->>'p_kicks')::bigint,0)) as kicks,
 sum(coalesce((p->>'p_couches')::bigint,0)) as couches,
 sum(coalesce((p->>'p_shots')::bigint,0)) as shots,
 sum(coalesce((p->>'p_hits')::bigint,0)) as hits,
 sum(coalesce((p->>'p_headshots')::bigint,0)) as headshots,
 sum(coalesce((p->>'p_teamkills')::bigint,0)) as teamkills,
 sum(coalesce((p->>'p_team_hits')::bigint,0)) as team_hits,
 sum(coalesce((p->>'p_team_damage')::bigint,0)) as team_damage,
 sum(coalesce((p->>'p_melee_damage')::bigint,0)) as melee_damage,
 sum(coalesce((p->>'p_mounted_damage')::bigint,0)) as mounted_damage,
 sum(coalesce((p->>'p_ranged_damage')::bigint,0)) as ranged_damage,
 sum(coalesce((p->>'p_kills_melee')::bigint,0)) as kills_melee,
 sum(coalesce((p->>'p_kills_ranged')::bigint,0)) as kills_ranged,
 sum(coalesce((p->>'p_kills_throwing')::bigint,0)) as kills_throwing,
 sum(coalesce((p->>'p_kills_mounted_melee')::bigint,0)) as kills_mounted_melee,
 sum(coalesce((p->>'p_kills_mounted_ranged')::bigint,0)) as kills_mounted_ranged,
 sum(coalesce((p->>'p_kills_mounted_throwing')::bigint,0)) as kills_mounted_throwing,
 sum(coalesce((p->>'p_deaths_melee')::bigint,0)) as deaths_melee,
 sum(coalesce((p->>'p_deaths_ranged')::bigint,0)) as deaths_ranged,
 sum(coalesce((p->>'p_deaths_throwing')::bigint,0)) as deaths_throwing,
 sum(coalesce((p->>'p_deaths_mounted_melee')::bigint,0)) as deaths_mounted_melee,
 sum(coalesce((p->>'p_deaths_mounted_ranged')::bigint,0)) as deaths_mounted_ranged,
 sum(coalesce((p->>'p_deaths_mounted_throwing')::bigint,0)) as deaths_mounted_throwing,
 sum(coalesce((p->>'p_suicides')::bigint,0)) as suicides,
 sum(coalesce((p->>'p_throwing_damage')::bigint,0)) as throwing_damage,
 sum(coalesce((p->>'p_mounted_ranged_damage')::bigint,0)) as mounted_ranged_damage,
 sum(coalesce((p->>'p_mounted_throwing_damage')::bigint,0)) as mounted_throwing_damage,
 sum(coalesce((p->>'p_throwing_shots')::bigint,0)) as throwing_shots,
 sum(coalesce((p->>'p_throwing_hits')::bigint,0)) as throwing_hits,
 sum(coalesce((p->>'p_throwing_headshots')::bigint,0)) as throwing_headshots,
 sum(coalesce((p->>'p_team_hits_received')::bigint,0)) as team_hits_received,
 sum(coalesce((p->>'p_team_damage_received')::bigint,0)) as team_damage_received,
 sum(coalesce((p->>'p_deaths_by_teammate')::bigint,0)) as deaths_by_teammate,
 sum(coalesce((p->>'p_alive_ms')::bigint,0)) as alive_ms,
 sum(coalesce((p->>'p_spawn_count')::bigint,0)) as spawn_count,
 sum(coalesce((p->>'p_score')::bigint,0)) as total_score,
 sum(coalesce((p->>'p_kills')::bigint,0)) as total_kills,
 sum(coalesce((p->>'p_deaths')::bigint,0)) as total_deaths,
 sum(coalesce((p->>'p_assists')::bigint,0)) as total_assists,
 count(*) filter(where (p->>'p_is_winner')::boolean) as rounds_won,
 count(*) filter(where (p->>'p_is_mvp')::boolean) as mvp_count,
 count(*) filter(where (p->>'p_first_kill')::boolean) as first_kills,
 count(*) filter(where (p->>'p_first_death')::boolean) as first_deaths,
 count(*) filter(where p->>'p_class_group'='Infantry') as rounds_as_infantry,
 count(*) filter(where p->>'p_class_group'='Ranged') as rounds_as_ranged,
 count(*) filter(where p->>'p_class_group'='Cavalry') as rounds_as_cavalry
 from public.ci_statistics_rounds r cross join lateral jsonb_array_elements(r.report->'players') p
 group by r.event_date,p->>'p_steam_id';
create or replace view public.ci_statistics_player_cultures with(security_invoker=true) as
 select r.event_date,p->>'p_steam_id' as steam_id,p->>'p_culture_id' as culture_id,count(*) as rounds_played,count(*) filter(where (p->>'p_is_winner')::boolean) as rounds_won
 from public.ci_statistics_rounds r cross join lateral jsonb_array_elements(r.report->'players') p group by r.event_date,p->>'p_steam_id',p->>'p_culture_id';
revoke all on public.ci_statistics_player_totals,public.ci_statistics_player_cultures from public,anon,authenticated;
grant select on public.ci_statistics_player_totals,public.ci_statistics_player_cultures to service_role;
commit;
