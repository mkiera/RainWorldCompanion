using System.Collections;
using System.Runtime.CompilerServices;
using RainWorldCompanion.LiveProtocol;
using RWCompanion.Mod;

namespace RainWorldCompanion.Tests
{
    public class MeadowRecoveryTests
    {
        [Fact]
        public void Destroyed_local_avatar_remains_recoverable_after_deregistration()
        {
            var fixture = new Fixture();
            Assert.Same(fixture.Creature, MeadowPlayers.FindLocal(fixture.PlayerId));
            fixture.DestroyAndDeregister();
            Assert.Same(fixture.Creature, MeadowPlayers.FindLocal(fixture.PlayerId));
            var snapshot = MeadowPlayers.Read()!.Single();
            Assert.True(snapshot.Dead);
            Assert.Null(snapshot.RoomId);
            MeadowPlayers.RestoreRecoveryRegistration(fixture.PlayerId, fixture.Game);
            Assert.Same(fixture.Avatar, RainMeadow.OnlineManager.recentEntities[fixture.Avatar.id]);
            Assert.Same(fixture.Avatar, RainMeadow.Extensions.GetOnlineObject(fixture.Creature));
            Assert.False(fixture.Avatar.realized);
            Assert.Single(fixture.Lobby.gameMode.avatars);
            MeadowPlayers.RestoreRecoveryRegistration(fixture.PlayerId, fixture.Game);
            Assert.Single(RainMeadow.OnlineManager.recentEntities);
        }

        [Fact]
        public void Recovery_does_not_restore_an_avatar_from_another_game()
        {
            var fixture = new Fixture();
            fixture.DestroyAndDeregister();
            Assert.Throws<InvalidOperationException>(() => MeadowPlayers.RestoreRecoveryRegistration(fixture.PlayerId, new object()));
            Assert.Empty(RainMeadow.OnlineManager.recentEntities);
        }

        [Theory]
        [InlineData("remote")]
        [InlineData("ownership")]
        [InlineData("pending")]
        [InlineData("left")]
        [InlineData("replaced")]
        public void Recovery_rejects_stale_or_unowned_avatars(string change)
        {
            var fixture = new Fixture();
            fixture.DestroyAndDeregister();
            if (change == "remote") fixture.Owner.isMe = false;
            if (change == "ownership") fixture.Avatar.isMine = false;
            if (change == "pending") fixture.Avatar.isPending = true;
            if (change == "left") fixture.Owner.hasLeft = true;
            if (change == "replaced") ((Settings)fixture.Lobby.clientSettings[fixture.Owner]!).avatars.Clear();
            Assert.Null(MeadowPlayers.FindLocal(fixture.PlayerId));
            Assert.Throws<InvalidOperationException>(() => MeadowPlayers.RestoreRecoveryRegistration(fixture.PlayerId, fixture.Game));
            Assert.Empty(RainMeadow.OnlineManager.recentEntities);
        }

        [Fact]
        public void Missing_remote_avatar_uses_the_clients_reported_death_state()
        {
            var fixture = new Fixture();
            fixture.DestroyAndDeregister();
            fixture.Owner.isMe = false;
            ((Settings)fixture.Lobby.clientSettings[fixture.Owner]!).Data.isDead = true;
            Assert.True(MeadowPlayers.Read()!.Single().Dead);
            Assert.Null(MeadowPlayers.FindLocal(fixture.PlayerId));
            ((Settings)fixture.Lobby.clientSettings[fixture.Owner]!).Data.isDead = false;
            Assert.Null(MeadowPlayers.Read()!.Single().Dead);
        }

        internal sealed class Fixture
        {
            internal readonly object Game = new();
            internal readonly Owner Owner = new();
            internal readonly Creature Creature;
            internal readonly Avatar Avatar;
            internal readonly Lobby Lobby;
            internal string PlayerId => "meadow:4:avatar";
            internal Fixture()
            {
                RainMeadow.OnlineManager.recentEntities.Clear();
                RainMeadow.OnlinePhysicalObject.map = new();
                Creature = new() { world = new() { game = Game } };
                Avatar = new() { apo = Creature, owner = Owner };
                Lobby = new() { owner = Owner };
                Lobby.clientSettings.Add(Owner, new Settings { avatars = [Avatar.id] });
                Lobby.participants.Add(Owner);
                Lobby.gameMode.avatars.Add(Avatar);
                RainMeadow.OnlineManager.lobby = Lobby;
                RainMeadow.OnlineManager.recentEntities.Add(Avatar.id, Avatar);
                RainMeadow.OnlinePhysicalObject.map.Add(Creature, Avatar);
            }
            internal void DestroyAndDeregister()
            {
                Creature.state.dead = true;
                Creature.slatedForDeletion = true;
                Avatar.primaryResource = null;
                RainMeadow.OnlineManager.recentEntities.Remove(Avatar.id);
                RainMeadow.OnlinePhysicalObject.map.Remove(Creature);
            }
        }

        internal sealed class Owner
        {
            public bool isMe = true;
            public bool hasLeft;
            public int inLobbyId = 4;
            public object id = new();
        }
        internal sealed class Identity
        {
            public object? FindEntity(bool quiet) => RainMeadow.OnlineManager.recentEntities[this];
            public override string ToString() => "avatar";
        }
        internal sealed class Avatar
        {
            public Identity id = new();
            public Creature apo = null!;
            public Owner owner = null!;
            public bool isMine = true;
            public bool isPending;
            public bool realized = true;
            public object? primaryResource = new();
        }
        internal sealed class Creature
        {
            public State state = new();
            public World world = null!;
            public bool slatedForDeletion;
        }
        internal sealed class State { public bool dead; }
        internal sealed class World { public object game = null!; }
        internal sealed class Settings
        {
            public bool inGame = true;
            public List<Identity> avatars = [];
            public RainMeadow.StoryClientSettingsData Data = new();
            public bool TryGetData(Type type, out object data) { data = Data; return true; }
        }
        internal sealed class Mode { public List<Avatar> avatars = []; }
        internal sealed class Lobby
        {
            public Owner owner = null!;
            public Hashtable clientSettings = new();
            public List<Owner> participants = [];
            public Mode gameMode = new();
        }
    }
}

namespace RainMeadow
{
    internal sealed class StoryClientSettingsData { public bool isDead; }
    internal static class OnlineManager
    {
        public static object? lobby;
        public static Hashtable recentEntities = new();
    }
    internal static class OnlinePhysicalObject
    {
        public static ConditionalWeakTable<object, object> map = new();
    }
    internal static class Extensions
    {
        public static object? GetOnlineObject(object creature) => OnlinePhysicalObject.map.TryGetValue(creature, out var entity) ? entity : null;
    }
}

namespace RWCompanion.Mod
{
    internal static class Plugin
    {
        internal static LivePlayer FromCreature(object? creature, string id, string name, bool local)
            => new() { Id = id, Name = name, IsLocal = local, Dead = GameAccess.Get(GameAccess.Get(creature, "state"), "dead") as bool? };
    }
}
