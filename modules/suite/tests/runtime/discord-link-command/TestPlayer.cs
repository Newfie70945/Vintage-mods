using Vintagestory.Common;
using Vintagestory.Server;
using Vintagestory.API.Common;
public class TestPlayer : ServerPlayer, Vintagestory.API.Server.IServerPlayer
{
    public static Fixture Fixture=null!;
    public new void SendMessage(int groupId,string message,EnumChatType type,string data=null!) => Fixture.Replies.Add(message);
    public override bool HasPrivilege(string code) => code=="chat";
    public TestPlayer() : base(null!, null!) { }
    public static TestPlayer Create()
    {
        var player = (TestPlayer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TestPlayer));
        var field = typeof(ServerPlayer).GetField("client", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var client = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(field.FieldType);
        field.FieldType.GetField("FallbackPlayerName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.SetValue(client, "Alice");
        field.SetValue(player, client);
        return player;
    }
    public override string PlayerUID => "test-player";
    public override IPlayerRole Role { get => new PlayerRole { Code = "player", Name = "Player" }; set { } }
}
