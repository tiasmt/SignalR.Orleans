﻿using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SignalR.Orleans.Core
{
    internal abstract class ConnectionGrain<TGrainState> : Grain<TGrainState>, IConnectionGrain
        where TGrainState : ConnectionState, new()
    {
        private readonly ILogger _logger;
        private IStreamProvider _streamProvider;
        private readonly HashSet<string> _connectionStreamToUnsubscribe = new HashSet<string>();
        private readonly TimeSpan _cleanupPeriod = TimeSpan.Parse(Constants.CONNECTION_STREAM_CLEANUP);

        protected ConnectionGrainKey KeyData;
        private IDisposable _cleanupTimer;

        internal ConnectionGrain(ILogger logger)
        {
            _logger = logger;
        }

        public override async Task OnActivateAsync()
        {
            KeyData = new ConnectionGrainKey(this.GetPrimaryKeyString());
            _streamProvider = GetStreamProvider(Constants.STREAM_PROVIDER);

            _cleanupTimer = RegisterTimer(
                _ => CleanupStreams(),
                State,
                _cleanupPeriod,
                _cleanupPeriod);

            if (State.Connections.Count == 0)
            {
                return;
            }

            var subscriptionTasks = new Dictionary<string, Task<StreamSubscriptionHandle<string>>>();
            foreach (var connection in State.Connections)
            {
                var clientDisconnectStream = GetClientDisconnectStream(connection);
                var subscriptions = await clientDisconnectStream.GetAllSubscriptionHandles();
                var subscription = subscriptions.FirstOrDefault();
                if (subscription == null)
                    continue;
                subscriptionTasks.Add(connection, subscription.ResumeAsync(async (connectionId, _) => await Remove(connectionId)));
            }
            await Task.WhenAll(subscriptionTasks.Values);
        }

        public override Task OnDeactivateAsync()
        {
            _cleanupTimer?.Dispose();
            return CleanupStreams();
        }

        public virtual async Task Add(string connectionId)
        {
            if (!State.Connections.Add(connectionId))
                return;

            var clientDisconnectStream = GetClientDisconnectStream(connectionId);
            await clientDisconnectStream.SubscribeAsync(async (connId, _) => await Remove(connId));
            await WriteStateAsync();
        }

        public virtual async Task Remove(string connectionId)
        {
            var shouldWriteState = State.Connections.Remove(connectionId);
            _connectionStreamToUnsubscribe.Add(connectionId);

            if (State.Connections.Count == 0)
            {
                await ClearStateAsync();
            }
            else if (shouldWriteState)
            {
                await WriteStateAsync();
            }
        }

        public virtual Task Send(Immutable<InvocationMessage> message)
            => SendAll(message, State.Connections);

        public Task SendExcept(string methodName, object[] args, IReadOnlyList<string> excludedConnectionIds)
        {
            var message = new Immutable<InvocationMessage>(new InvocationMessage(methodName, args));
            return SendAll(message, State.Connections.Where(x => !excludedConnectionIds.Contains(x)).ToList());
        }

        public Task<int> Count()
            => Task.FromResult(State.Connections.Count);

        protected Task SendAll(Immutable<InvocationMessage> message, IReadOnlyCollection<string> connections)
        {
            _logger.LogDebug("Sending message to {hubName}.{targetMethod} on group {groupId} to {connectionsCount} connection(s)",
                KeyData.HubName, message.Value.Target, KeyData.Id, connections.Count);

            foreach (var connection in connections)
            {
                GrainFactory.GetClientGrain(KeyData.HubName, connection)
                    .InvokeOneWay(x => x.Send(message));
            }

            return Task.CompletedTask;
        }

        private async Task CleanupStreams()
        {
            if (_connectionStreamToUnsubscribe.Count > 0)
            {
                foreach (var connectionId in _connectionStreamToUnsubscribe.ToList())
                {
                    var handles = await GetClientDisconnectStream(connectionId).GetAllSubscriptionHandles();
                    var unsubscribes = handles.Select(x => x.UnsubscribeAsync()).ToList();
                    await Task.WhenAll(unsubscribes);
                    _connectionStreamToUnsubscribe.Remove(connectionId);
                }
            }
        }

        private IAsyncStream<string> GetClientDisconnectStream(string connectionId)
            => _streamProvider.GetStream<string>(Constants.CLIENT_DISCONNECT_STREAM_ID, connectionId);
    }

    internal abstract class ConnectionState
    {
        public HashSet<string> Connections { get; set; } = new HashSet<string>();
    }
}