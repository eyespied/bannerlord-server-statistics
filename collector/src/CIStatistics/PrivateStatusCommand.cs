using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CIStatistics.Stats;
using NetworkMessages.FromClient;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Network.Messages;

namespace CIStatistics
{
    // Base handlers run before native ChatBox handlers. Restrict the incoming
    // message's ReceiverList before native forwarding; keep the packet valid.
    internal static class PrivateStatusCommand
    {
        private static bool _registered;
        private static readonly PropertyInfo? AllReceivers = typeof(PlayerMessageAll).GetProperty("ReceiverList");
        private static readonly PropertyInfo? TeamReceivers = typeof(PlayerMessageTeam).GetProperty("ReceiverList");
        private sealed class LastRequest { public DateTimeOffset At; }
        private static readonly ConditionalWeakTable<NetworkCommunicator, LastRequest> Requests = new();
        public static void Update()
        {
            if (_registered || !GameNetwork.IsDedicatedServer || !GameNetwork.IsServer) return;
            // Never register a command that cannot be made private on this build.
            if (AllReceivers?.GetSetMethod(true) == null || TeamReceivers?.GetSetMethod(true) == null) return;
            var register = new GameNetwork.NetworkMessageHandlerRegisterer(GameNetwork.NetworkMessageHandlerRegisterer.RegisterMode.Add);
            register.RegisterBaseHandler<PlayerMessageAll>(HandleAll);
            register.RegisterBaseHandler<PlayerMessageTeam>(HandleTeam);
            _registered = true;
        }
        public static void Unregister()
        {
            if (!_registered) return;
            var register = new GameNetwork.NetworkMessageHandlerRegisterer(GameNetwork.NetworkMessageHandlerRegisterer.RegisterMode.Remove);
            register.RegisterBaseHandler<PlayerMessageAll>(HandleAll);
            register.RegisterBaseHandler<PlayerMessageTeam>(HandleTeam);
            _registered = false;
        }
        internal static bool IsCommand(string text) => string.Equals(text.Trim(), "/stats", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text.Trim(), "!stats", StringComparison.OrdinalIgnoreCase);
        private static bool HandleAll(NetworkCommunicator peer, GameNetworkMessage message)
        {
            var chat = (PlayerMessageAll)message;
            return Handle(peer, chat.Message, chat, AllReceivers!);
        }
        private static bool HandleTeam(NetworkCommunicator peer, GameNetworkMessage message)
        {
            var chat = (PlayerMessageTeam)message;
            return Handle(peer, chat.Message, chat, TeamReceivers!);
        }
        private static bool Handle(NetworkCommunicator peer, string text, object message, PropertyInfo receivers)
        {
            if (!IsCommand(text)) return true;
            // Empty receivers suppress the original command in both all/team chat.
            receivers.SetValue(message, new List<VirtualPlayer>());
            var last = Requests.GetOrCreateValue(peer);
            var now = DateTimeOffset.UtcNow;
            if ((now - last.At).TotalSeconds < 3) return true;
            last.At = now;
            string status;
            var config = StatsConfig.Load();
            if (!config.Enabled) status = "Disabled in server config.";
            else if (!config.IsReady) status = "Not ready: server upload configuration is missing or invalid.";
            else status = Mission.Current?.GetMissionBehavior<RoundCombatTracker>()?.StatusText ?? "Ready; waiting for a mission.";
            status += " " + RoundUploader.StatusText;
            GameNetwork.BeginModuleEventAsServer(peer);
            GameNetwork.WriteMessage(new NetworkMessages.FromServer.ServerMessage("[Statistics] " + status));
            GameNetwork.EndModuleEventAsServer();
            return true;
        }
    }
}
