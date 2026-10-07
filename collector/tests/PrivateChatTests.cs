using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Network.Messages;
namespace TaleWorlds.Core { public sealed class VirtualPlayer { } }
namespace TaleWorlds.MountAndBlade.Network.Messages {
 public class GameNetworkMessage { public delegate bool ClientMessageHandlerDelegate<T>(NetworkCommunicator peer,T message); }
}
namespace TaleWorlds.MountAndBlade {
 public sealed class NetworkCommunicator { }
 public sealed class Mission { public static Mission? Current; public T? GetMissionBehavior<T>() where T:class => null; }
 public static class GameNetwork {
  public static bool IsDedicatedServer=true,IsServer=true;
  public static NetworkCommunicator? LastRecipient; public static GameNetworkMessage? LastReply;
  public static readonly Dictionary<Type,GameNetworkMessage.ClientMessageHandlerDelegate<GameNetworkMessage>> Handlers=new();
  public sealed class NetworkMessageHandlerRegisterer {
   public enum RegisterMode { Add,Remove }
   private readonly RegisterMode mode;public NetworkMessageHandlerRegisterer(RegisterMode value){mode=value;}
   public void RegisterBaseHandler<T>(GameNetworkMessage.ClientMessageHandlerDelegate<GameNetworkMessage> handler){if(mode==RegisterMode.Add)Handlers[typeof(T)]=handler;else Handlers.Remove(typeof(T));}
  }
  public static void BeginModuleEventAsServer(NetworkCommunicator peer){LastRecipient=peer;}
  public static void WriteMessage(GameNetworkMessage message){LastReply=message;}
  public static void EndModuleEventAsServer(){ }
 }
}
namespace CIStatistics.Stats { internal sealed class RoundCombatTracker { internal string StatusText=>"Recording live round 1."; } }
namespace NetworkMessages.FromClient {
 public sealed class PlayerMessageAll : GameNetworkMessage { public string Message {get;private set;} public List<VirtualPlayer>? ReceiverList{get;private set;} public PlayerMessageAll(string message){Message=message;} }
 public sealed class PlayerMessageTeam : GameNetworkMessage { public string Message {get;private set;} public List<VirtualPlayer>? ReceiverList{get;private set;} public PlayerMessageTeam(string message){Message=message;} }
}
namespace NetworkMessages.FromServer { public sealed class ServerMessage : GameNetworkMessage { public string Text;public ServerMessage(string text){Text=text;} } }
internal static class PrivateChatTests {
 private static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
 public static void Run(){
  CIStatistics.PrivateStatusCommand.Update();Assert(GameNetwork.Handlers.Count==2,"All and team base handlers registered");
  var peer=new NetworkCommunicator();var command=new NetworkMessages.FromClient.PlayerMessageAll(" /STATS ");
  Assert(GameNetwork.Handlers[command.GetType()](peer,command),"Packet remains valid");
  Assert(command.ReceiverList?.Count==0,"Native all-chat relay receives empty recipient list");
  Assert(GameNetwork.LastRecipient==peer&&GameNetwork.LastReply is NetworkMessages.FromServer.ServerMessage,"Reply targets author only");
  GameNetwork.LastReply=null;var ordinary=new NetworkMessages.FromClient.PlayerMessageAll("hello");
  Assert(GameNetwork.Handlers[ordinary.GetType()](peer,ordinary)&&ordinary.ReceiverList==null&&GameNetwork.LastReply==null,"Ordinary chat untouched");
  var team=new NetworkMessages.FromClient.PlayerMessageTeam("!stats");var other=new NetworkCommunicator();
  Assert(GameNetwork.Handlers[team.GetType()](other,team)&&team.ReceiverList?.Count==0&&GameNetwork.LastRecipient==other,"Team chat command and reply private");
  CIStatistics.PrivateStatusCommand.Unregister();Assert(GameNetwork.Handlers.Count==0,"Handlers unregister cleanly");
  Console.WriteLine("PASS: private command interception, all/team recipient suppression, author-only reply and ordinary-chat preservation");
 }
}
