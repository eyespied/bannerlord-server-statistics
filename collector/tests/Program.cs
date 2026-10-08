using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CIStatistics.Stats;
namespace TaleWorlds.Library { public static class Debug { public static void Print(string value)=>Console.WriteLine(value); } }
namespace TaleWorlds.MountAndBlade { }
internal sealed class FakeHandler : HttpMessageHandler {
 public HttpStatusCode Status=HttpStatusCode.ServiceUnavailable;
 public string Body="{\"status\":\"accepted\"}";
 public string? LastBody; public string? Authorization;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){LastBody=await request.Content!.ReadAsStringAsync(token);Authorization=request.Headers.Authorization?.ToString();return new HttpResponseMessage(Status){Content=new StringContent(Body)};}
}
internal static class Program {
 static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
 static async Task Main(){
  string folder=Path.Combine(Path.GetTempPath(),"ci-statistics-test-"+Guid.NewGuid().ToString("N"));
  var config=new StatsConfig{Enabled=true,IngestUrl="https://example.test/ingest",IngestToken=new string('a',43),QueueDirectory=folder};
  var handler=new FakeHandler();var first=new RoundUploader(config,handler,false);
  var report=new RoundReport{match_id=Guid.NewGuid(),round_number=1,started_at=DateTimeOffset.UtcNow,ended_at=DateTimeOffset.UtcNow};
  first.EnqueueRound(report);Assert(Directory.GetFiles(folder,"*.json").Length==1,"Round persisted before upload");
  await first.DrainOnce();Assert(Directory.GetFiles(folder,"*.json").Length==1,"HTTP failure retains round");
  string? body=handler.LastBody;
  handler.Status=HttpStatusCode.OK;handler.Body="<html>Login required</html>";await first.DrainOnce();Assert(Directory.GetFiles(folder,"*.json").Length==1,"Unexpected success page does not discard report");
  var secondHandler=new FakeHandler{Status=HttpStatusCode.OK};var restarted=new RoundUploader(config,secondHandler,false);
  await restarted.DrainOnce();Assert(Directory.GetFiles(folder,"*.json").Length==0,"Restart replays persisted round");Assert(body==secondHandler.LastBody,"Retry preserves identity and timestamp");
  Assert(secondHandler.Authorization=="Bearer "+config.IngestToken,"Only server token sent");
  first.EnqueueRound(report);handler.Status=HttpStatusCode.Conflict;await first.DrainOnce();Assert(Directory.GetFiles(folder,"*.rejected").Length==1,"Conflict preserved for investigation");
  var snapshot=new PlayerRoundSnapshot{BaselineKills=10,BaselineDeaths=4,BaselineAssists=2,BaselineScore=100};snapshot.ApplyMissionPeerDeltas(12,5,3,135);Assert(snapshot.Kills==2&&snapshot.Score==35,"Round deltas do not count earlier rounds");
  var spectator=new PlayerRoundSnapshot();Assert(!spectator.HasAssignedTeam,"Unassigned peers cannot enter round reports");
  spectator.TeamSide="None";Assert(!spectator.HasAssignedTeam,"Spectators cannot enter round reports");
  spectator.TeamSide="Defender";spectator.TeamSide="None";spectator.TeamSide="";
  Assert(spectator.HasAssignedTeam&&spectator.TeamSide=="Defender","Leaving a team retains the last assigned side");
  Directory.Delete(folder,true);Console.WriteLine("PASS: durable queue, failure/restart replay, immutable retry, credential transport, rejection evidence, round deltas and spectator filtering");
  PrivateChatTests.Run();
 }
}
