﻿/* Copyright 2013-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver.Core.Async;
using MongoDB.Driver.Core.Clusters.ServerSelectors;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.Servers;

namespace MongoDB.Driver.Core.Clusters
{
    /// <summary>
    /// Represents a multi server cluster.
    /// </summary>
    public abstract class MultiServerCluster : ICluster
    {
        // fields
        private readonly CancellationTokenSource _backgroundTaskCancellationTokenSource = new CancellationTokenSource();
        private readonly ClusterMonitorSpec _clusterMonitorSpec = new ClusterMonitorSpec();
        private ClusterDescription _description;
        private TaskCompletionSource<bool> _descriptionChangedTaskCompletionSource;
        private bool _disposed;
        private readonly IClusterListener _listener;
        private readonly object _lock = new object();
        private readonly IServerSelector _randomServerSelector = new RandomServerSelector();
        private readonly AsyncQueue<ServerDescriptionChangedEventArgs> _serverDescriptionChangedQueue = new AsyncQueue<ServerDescriptionChangedEventArgs>();
        private readonly IServerFactory _serverFactory;
        private readonly List<IRootServer> _servers = new List<IRootServer>();
        private readonly ClusterSettings _settings;

        // constructors
        protected MultiServerCluster(ClusterSettings settings, IServerFactory serverFactory, IClusterListener listener)
        {
            _settings = Ensure.IsNotNull(settings, "settings");
            _serverFactory = Ensure.IsNotNull(serverFactory, "serverFactory");
            _listener = listener;

            _description = ClusterDescription.CreateUninitialized(settings.ClusterType);
            _descriptionChangedTaskCompletionSource = new TaskCompletionSource<bool>();
        }

        // events
        public event EventHandler<ClusterDescriptionChangedEventArgs> DescriptionChanged;

        // properties
        public ClusterDescription Description
        {
            get
            {
                ThrowIfDisposed();
                lock (_lock)
                {
                    return _description;
                }
            }
        }

        public ClusterSettings Settings
        {
            get { return _settings; }
        }

        // methods
        internal void AddServer(IRootServer server)
        {
            lock (_lock)
            {
                if (_servers.Any(n => n.EndPoint.Equals(server.EndPoint)))
                {
                    var message = string.Format("The cluster already contains a server for end point: {0}.", DnsEndPointParser.ToString(server.EndPoint));
                    throw new ArgumentException(message, "server");
                }

                _servers.Add(server);
            }

            server.DescriptionChanged += ServerDescriptionChangedHandler;
            server.Initialize();

            if (_listener != null)
            {
                var args = new ServerAddedEventArgs(server.Description);
                _listener.ServerAdded(args);
            }
        }

        private async Task BackgroundTask()
        {
            var cancellationToken = _backgroundTaskCancellationTokenSource.Token;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var eventArgs = await _serverDescriptionChangedQueue.DequeueAsync(); // TODO: add timeout and cancellationToken to DequeueAsync
                    ProcessServerDescriptionChanged(eventArgs);
                }
            }
            catch (TaskCanceledException)
            {
                // ignore TaskCanceledException
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (disposing)
                {
                    if (!_disposed)
                    {
                        _backgroundTaskCancellationTokenSource.Cancel();
                        foreach (var server in _servers)
                        {
                            server.Dispose();
                        }
                        _backgroundTaskCancellationTokenSource.Dispose();
                    }
                }
                _disposed = true;
            }
        }

        public async Task<ClusterDescription> GetDescriptionAsync(int minimumRevision = 0, TimeSpan timeout = default(TimeSpan), CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            var slidingTimeout = new SlidingTimeout(timeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClusterDescription description;
                Task descriptionChangedTask;
                lock (_lock)
                {
                    description = _description;
                    descriptionChangedTask = _descriptionChangedTaskCompletionSource.Task;
                }

                if (description.Revision >= minimumRevision)
                {
                    return description;
                }

                await descriptionChangedTask.WithTimeout(slidingTimeout, cancellationToken);
            }
        }

        public IServer GetServer(DnsEndPoint endPoint)
        {
            lock (_lock)
            {
                return _servers.Where(s => s.EndPoint.Equals(endPoint)).FirstOrDefault();
            }
        }

        public void Initialize()
        {
            foreach (var endPoint in _settings.EndPoints)
            {
                var server = _serverFactory.Create(endPoint);
                AddServer(server);
            }
            BackgroundTask().LogUnobservedExceptions();
        }

        public async Task<IServer> SelectServerAsync(IServerSelector selector, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ensure.IsNotNull(selector, "selector");
            var slidingTimeout = new SlidingTimeout(timeout);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClusterDescription description;
                Task descriptionChangedTask;
                lock (_lock)
                {
                    description = _description;
                    descriptionChangedTask = _descriptionChangedTaskCompletionSource.Task;
                }

                var connectedServers = description.Servers.Where(s => s.State == ServerState.Connected);
                var selectedServers = selector.SelectServers(_description, connectedServers).ToList();

                while (selectedServers.Any())
                {
                    var server = selectedServers.Count == 1 ?
                        selectedServers[0] :
                        _randomServerSelector.SelectServers(_description, selectedServers).Single();

                    IRootServer rootServer;
                    if (TryGetServer(server.EndPoint, out rootServer))
                    {
                        return rootServer;
                    }

                    selectedServers.Remove(server);
                }

                await descriptionChangedTask.WithTimeout(slidingTimeout, cancellationToken);
            }
        }

        private void ServerDescriptionChangedHandler(object sender, ServerDescriptionChangedEventArgs args)
        {
            var server = (IServer)sender;
            _serverDescriptionChangedQueue.Enqueue(args);
        }

        protected virtual void OnDescriptionChanged(ClusterDescription oldDescription, ClusterDescription newDescription)
        {
            ClusterDescriptionChangedEventArgs args = null;

            if (_listener != null)
            {
                args = new ClusterDescriptionChangedEventArgs(oldDescription, newDescription);
                _listener.ClusterDescriptionChanged(args);
            }

            var handler = DescriptionChanged;
            if (handler != null)
            {
                if (args == null)
                {
                    args = new ClusterDescriptionChangedEventArgs(oldDescription, newDescription);
                }
                handler(this, args);
            }
        }

        private void ProcessServerDescriptionChanged(ServerDescriptionChangedEventArgs eventArgs)
        {
            ClusterDescription oldClusterDescription = null;
            ClusterDescription newClusterDescription = null;
            TaskCompletionSource<bool> oldDescriptionChangedTaskCompletionSource = null;

            var actions = _clusterMonitorSpec.Transition(_description, eventArgs.NewServerDescription);
            lock (_lock)
            {
                foreach (var action in actions)
                {
                    // TODO: implement action
                }

                oldDescriptionChangedTaskCompletionSource = _descriptionChangedTaskCompletionSource;
                _description = newClusterDescription.WithRevision(oldClusterDescription.Revision + 1);
                _descriptionChangedTaskCompletionSource = new TaskCompletionSource<bool>();
            }

            OnDescriptionChanged(oldClusterDescription, newClusterDescription);
            oldDescriptionChangedTaskCompletionSource.TrySetResult(true);

            if (newClusterDescription.Type == ClusterType.ReplicaSet && !object.Equals(oldClusterDescription.ReplicaSetConfig, newClusterDescription.ReplicaSetConfig))
            {
                ProcessReplicaSetConfigChanged(newClusterDescription.ReplicaSetConfig);
            }
        }

        private void ProcessReplicaSetConfigChanged(ReplicaSetConfig newConfig)
        {
            foreach (var endPoint in newConfig.Members)
            {
                if (!_servers.Any(n => n.EndPoint.Equals(endPoint)))
                {
                    var server = _serverFactory.Create(endPoint);
                    AddServer(server);
                }
            }

            foreach (var server in _servers.ToList())
            {
                if (!newConfig.Members.Contains(server.EndPoint))
                {
                    RemoveServer(server);
                }
            }
        }

        protected void RemoveServer(IRootServer server)
        {
            server.DescriptionChanged -= ServerDescriptionChangedHandler;
            var endPoint = server.EndPoint;

            lock (_lock)
            {
                _servers.Remove(server);
            }

            server.Dispose();

            if (_listener != null)
            {
                var args = new ServerRemovedEventArgs(endPoint);
                _listener.ServerRemoved(args);
            }
        }

        protected void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        private bool TryGetServer(DnsEndPoint endPoint, out IRootServer server)
        {
            lock (_lock)
            {
                server = _servers.FirstOrDefault(s => s.EndPoint.Equals(endPoint));
                return server != null;
            }
        }
    }
}