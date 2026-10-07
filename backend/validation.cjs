"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.booleanFields = exports.numberFields = void 0;
exports.validateReport = validateReport;
exports.readBoundedJson = readBoundedJson;
// This is a round report, never a database query or an arbitrary table selector.
exports.numberFields = ["p_score", "p_kills", "p_deaths", "p_assists", "p_horse_damage", "p_horse_kills", "p_kicks", "p_couches", "p_shots", "p_hits", "p_headshots", "p_teamkills", "p_team_hits", "p_team_damage", "p_melee_damage", "p_mounted_damage", "p_ranged_damage", "p_kills_melee", "p_kills_ranged", "p_kills_throwing", "p_kills_mounted_melee", "p_kills_mounted_ranged", "p_kills_mounted_throwing", "p_deaths_melee", "p_deaths_ranged", "p_deaths_throwing", "p_deaths_mounted_melee", "p_deaths_mounted_ranged", "p_deaths_mounted_throwing", "p_suicides", "p_throwing_damage", "p_mounted_ranged_damage", "p_mounted_throwing_damage", "p_throwing_shots", "p_throwing_hits", "p_throwing_headshots", "p_team_hits_received", "p_team_damage_received", "p_deaths_by_teammate", "p_alive_ms", "p_spawn_count"];
exports.booleanFields = ["p_is_mvp", "p_is_winner", "p_first_kill", "p_first_death"];
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function str(v, max) { if (typeof v !== 'string' || v.length > max || /[\u0000-\u001f]/.test(v))
    throw Error('Invalid text'); return v; }
function validateReport(input, now = Date.now()) {
    if (!input || typeof input !== 'object' || Array.isArray(input))
        throw Error('Invalid report');
    const b = input;
    if (b.schema_version !== 1 || typeof b.match_id !== 'string' || !uuid.test(b.match_id) || !Number.isInteger(b.round_number) || Number(b.round_number) < 1 || Number(b.round_number) > 10000)
        throw Error('Invalid round identity');
    const start = str(b.started_at, 40), end = str(b.ended_at, 40), a = Date.parse(start), z = Date.parse(end);
    if (!/(Z|[+-]\d{2}:\d{2})$/.test(start) || !/(Z|[+-]\d{2}:\d{2})$/.test(end) || !Number.isFinite(a) || !Number.isFinite(z) || z < a || z > now + 300000 || z - a > 86400000)
        throw Error('Invalid round timestamps');
    if (!Array.isArray(b.players) || b.players.length < 1 || b.players.length > 600)
        throw Error('Invalid player count');
    const ids = new Set();
    const players = b.players.map(v => {
        if (!v || typeof v !== 'object' || Array.isArray(v))
            throw Error('Invalid player');
        const p = v;
        const out = {};
        const id = str(p.p_steam_id, 100);
        if (!id || ids.has(id) || !/^[A-Za-z0-9_.:-]+$/.test(id))
            throw Error('Invalid player identity');
        ids.add(id);
        out.p_steam_id = id;
        out.p_display_name = str(p.p_display_name, 200);
        out.p_culture_id = str(p.p_culture_id, 100);
        if (!['Attacker', 'Defender'].includes(String(p.p_team_side)) || !['Infantry', 'Ranged', 'Cavalry'].includes(String(p.p_class_group)))
            throw Error('Invalid team or class');
        out.p_team_side = p.p_team_side;
        out.p_class_group = p.p_class_group;
        for (const k of exports.numberFields) {
            const n = p[k];
            if (!Number.isSafeInteger(n) || Number(n) < 0 || Number(n) > (k === 'p_alive_ms' ? 86400000 : 10000000))
                throw Error('Invalid statistic');
            out[k] = n;
        }
        for (const k of exports.booleanFields) {
            if (typeof p[k] !== 'boolean')
                throw Error('Invalid statistic');
            out[k] = p[k];
        }
        return out;
    });
    return { schema_version: 1, match_id: b.match_id.toLowerCase(), round_number: Number(b.round_number), started_at: new Date(a).toISOString(), ended_at: new Date(z).toISOString(), map_id: str(b.map_id, 200), game_type: str(b.game_type, 100), players };
}
async function readBoundedJson(request, max = 1048576) {
    if (Number(request.headers.get('content-length')) > max)
        throw Error('Body too large');
    const reader = request.body?.getReader();
    if (!reader)
        throw Error('Empty body');
    let size = 0;
    const chunks = [];
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done)
                break;
            size += value.length;
            if (size > max)
                throw Error('Body too large');
            chunks.push(value);
        }
    }
    finally {
        await reader.cancel().catch(() => { });
    }
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}
