﻿using System;
using System.Threading.Tasks;
using Proto.TestFixtures;
using Xunit;

namespace Proto.Persistence.Tests
{
    public class ExamplePersistentActorTests
    {
        private const int InitialState = 1;

        [Fact]
        public async void EventsAreSavedToPersistence()
        {
            var (pid, _, actorName, providerState) = CreateTestActor();
            pid.Tell(new Multiply { Amount = 2 });
            await providerState
                .GetEventsAsync(actorName, 0, o =>
                {
                    Assert.IsType(typeof(Multiplied), o);
                    Assert.Equal(2, (o as Multiplied).Amount);
                });
        }

        [Fact]
        public async void SnapshotsAreSavedToPersistence()
        {
            var (pid, _, actorName, providerState) = CreateTestActor();
            pid.Tell(new Multiply { Amount = 10 });
            pid.Tell(new RequestSnapshot());
            var (snapshot, _) = await providerState.GetSnapshotAsync(actorName);
            var snapshotState = snapshot as State;
            Assert.Equal(10, snapshotState.Value);
        }

        [Fact]
        public async void SnapshotsCanBeDeleted()
        {
            var (pid, _, actorName, providerState) = CreateTestActor();
            pid.Tell(new Multiply { Amount = 10 });
            pid.Tell(new RequestSnapshot());
            await providerState.DeleteSnapshotsAsync(actorName, 0);
            var (snapshot, _) = await providerState.GetSnapshotAsync(actorName);
            Assert.Null(snapshot);
        }

        [Fact]
        public async void GivenEventsOnly_StateIsRestoredFromEvents()
        {
            var (pid, props, actorName, _) = CreateTestActor();
            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new Multiply { Amount = 2 });
            var state = await RestartActorAndGetState(pid, props, actorName);
            Assert.Equal(InitialState * 2 * 2, state);
        }

        [Fact]
        public async void GivenASnapshotOnly_StateIsRestoredFromTheSnapshot()
        {
            var (pid, props, actorName, providerState) = CreateTestActor();
            await providerState.PersistSnapshotAsync(actorName, 0, new State { Value = 10 });
            var state = await RestartActorAndGetState(pid, props, actorName);
            Assert.Equal(10, state);
        }

        [Fact]
        public async void GivenEventsThenASnapshot_StateShouldBeRestoredFromTheSnapshot()
        {
            var (pid, props, actorName, _) = CreateTestActor();
            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new RequestSnapshot());
            var state = await RestartActorAndGetState(pid, props, actorName);
            Assert.Equal(InitialState * 2 * 2, state);
        }

        [Fact]
        public async void GivenASnapshotAndSubsequentEvents_StateShouldBeRestoredFromSnapshotAndSubsequentEvents()
        {
            var (pid, props, actorName, _) = CreateTestActor();
            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new RequestSnapshot());
            pid.Tell(new Multiply { Amount = 4 });
            pid.Tell(new Multiply { Amount = 8 });
            var state = await RestartActorAndGetState(pid, props, actorName);
            Assert.Equal(InitialState * 2 * 2 * 4 * 8, state);
        }

        [Fact]
        public async void GivenASnapshotAndEvents_WhenSnapshotDeleted_StateShouldBeRestoredFromOriginalEvents()
        {
            var (pid, props, actorName, providerState) = CreateTestActor();

            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new Multiply { Amount = 2 });
            pid.Tell(new RequestSnapshot());
            pid.Tell(new Multiply { Amount = 4 });
            pid.Tell(new Multiply { Amount = 8 });
            await providerState.DeleteSnapshotsAsync(actorName, 3);
            
            var state = await RestartActorAndGetState(pid, props, actorName);
            Assert.Equal(InitialState * 2 * 2 * 4 * 8, state);
        }

        private (PID pid, Props props, string actorName, IProviderState providerState) CreateTestActor()
        {
            var actorName = Guid.NewGuid().ToString();
            var inMemoryProviderState = new InMemoryProviderState();
            var provider = new InMemoryProvider(inMemoryProviderState);
            var props = Actor.FromProducer(() => new ExamplePersistentActor())
                .WithReceiveMiddleware(Persistence.Using(provider))
                .WithMailbox(() => new TestMailbox());
            var pid = Actor.SpawnNamed(props, actorName);
            return (pid, props, actorName, inMemoryProviderState);
        }

        private async Task<int> RestartActorAndGetState(PID pid, Props props, string actorName)
        {
            pid.Stop();
            pid = Actor.SpawnNamed(props, actorName);
            return await pid.RequestAsync<int>(new GetState());
        }
    }

    internal class State
    {
        public int Value { get; set; }
    }

    internal class GetState { }

    internal class Multiply
    {
        public int Amount { get; set; }
    }

    internal class Multiplied
    {
        public int Amount { get; set; }
    }

    internal class ExamplePersistentActor : IPersistentActor
    {
        private State _state = new State{Value = 1};
        public Persistence Persistence { get; set; }

        public async Task ReceiveAsync(IContext context)
        {
            switch (context.Message)
            {
                case GetState msg:
                    context.Sender.Tell(_state.Value);
                    break;
                case RecoverSnapshot msg:
                    if (msg.Snapshot is State ss)
                    {
                        _state = ss;
                    }
                    break;
                case RecoverEvent msg:
                    UpdateState(msg.Event);
                    break;
                case PersistedEvent msg:
                    UpdateState(msg.Event);
                    break;
                case RequestSnapshot msg:
                    await Persistence.PersistSnapshotAsync(new State { Value = _state.Value });
                    break;
                case Multiply msg:
                    await Persistence.PersistEventAsync(new Multiplied { Amount = msg.Amount });
                    break;
            }
        }

        private void UpdateState(object message)
        {
            switch (message)
            {
                case Multiplied msg:
                    _state.Value = _state.Value * msg.Amount;
                    break;
            }
        }
    }
}
