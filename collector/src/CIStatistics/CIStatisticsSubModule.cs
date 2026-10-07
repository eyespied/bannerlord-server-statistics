using CIStatistics.Stats;
using TaleWorlds.MountAndBlade;
namespace CIStatistics {
 public sealed class CIStatisticsSubModule : MBSubModuleBase {
  protected override void OnApplicationTick(float dt) {
   base.OnApplicationTick(dt);
   PrivateStatusCommand.Update();
  }
  public override void OnGameEnd(TaleWorlds.Core.Game game) {
   PrivateStatusCommand.Unregister();
   base.OnGameEnd(game);
  }
  public override void OnBeforeMissionBehaviorInitialize(Mission mission) {
   base.OnBeforeMissionBehaviorInitialize(mission);
   var config=StatsConfig.Load();
   if(GameNetwork.IsDedicatedServer && config.IsReady && mission.GetMissionBehavior<RoundCombatTracker>()==null)
    mission.AddMissionBehavior(new RoundCombatTracker(config));
  }
 }
}
