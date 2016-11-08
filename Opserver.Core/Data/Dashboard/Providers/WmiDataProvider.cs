﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace StackExchange.Opserver.Data.Dashboard.Providers
{
    partial class WmiDataProvider : DashboardDataProvider<WMISettings>
    {
        private readonly WMISettings _config;
        private readonly List<WmiNode> _wmiNodes = new List<WmiNode>();
        private Dictionary<string, WmiNode> _wmiNodeLookup;

        public WmiDataProvider(WMISettings settings) : base(settings)
        {
            _config = settings;

            Task.Run(() => this.Initialize());
        }

        private void Initialize()
        {
            this._wmiNodes.AddRange(InitNodeList(_config.Nodes).OrderBy(x => x.Endpoint).ToList());

            // Do this ref cast list once
            this.AllNodes.AddRange(this._wmiNodes.Cast<Node>().ToList());
            // For fast lookups
            this._wmiNodeLookup = new Dictionary<string, WmiNode>(this._wmiNodes.Count);
            foreach (var n in this._wmiNodes)
            {
                this._wmiNodeLookup[n.Id] = n;
            }
        }

        /// <summary>
        /// Make list of nodes as per configuration. 
        /// When adding, a node's ip address is resolved via Dns.
        /// </summary>
        private IEnumerable<WmiNode> InitNodeList(IList<string> names)
        {
            var nodesList = new List<WmiNode>(names.Count);
            var exclude = Current.Settings.Dashboard.ExcludePatternRegex;

            var staticDataCaches = new List<Task>();
            var dynamicDataCaches = new List<Task>();

            foreach (var nodeName in names)
            {
                if (exclude?.IsMatch(nodeName) ?? false)
                {
                    continue;
                }

                var node = new WmiNode(nodeName)
                {
                    Config = _config,
                    DataProvider = this
                };

                try
                {
                    var hostEntry = Dns.GetHostEntry(node.Name);
                    if (hostEntry.AddressList.Any())
                    {
                        node.Ip = hostEntry.AddressList[0].ToString();
                        node.Status = NodeStatus.Active;
                    }
                    else
                    {
                        node.Status = NodeStatus.Unreachable;
                    }
                }
                catch (Exception)
                {
                    node.Status = NodeStatus.Unreachable;
                }

                var staticDataCache = ProviderCache(
                    () => node.PollNodeInfoAsync(),
                    _config.StaticDataTimeoutSeconds.Seconds(),
                    memberName: node.Name + "-Static");
                node.Caches.Add(staticDataCache);

                var dynamicDataCache = this.ProviderCache(
                    () => node.PollStats(),
                    this._config.DynamicDataTimeoutSeconds.Seconds(),
                    memberName: node.Name + "-Dynamic");
                node.Caches.Add(dynamicDataCache);

                staticDataCaches.Add(staticDataCache.PollAsync(true));
                dynamicDataCaches.Add(dynamicDataCache.PollAsync(true));

                nodesList.Add(node);
            }

            // Force update static host data, incuding os info, volumes, interfaces.
            {
                var caches = staticDataCaches.ToArray();
                Task.WaitAll(caches);
            }

            // Force first dynamic polling of utilization.
            // This is needed because we use PerfRawData performance counters and we need this as a first starting point for our calculations.
            {
                var caches = dynamicDataCaches.ToArray();
                Task.WaitAll(caches);
            }

            return nodesList;
        }

        private WmiNode GetWmiNodeById(string id)
        {
            WmiNode n;
            return _wmiNodeLookup.TryGetValue(id, out n) ? n : null;
        }

        public override int MinSecondsBetweenPolls => 10;

        public override string NodeType => "WMI";

        public override IEnumerable<Cache> DataPollers => _wmiNodes.SelectMany(x => x.Caches);

        protected override IEnumerable<MonitorStatus> GetMonitorStatus()
        {
            yield break;
        }

        protected override string GetMonitorStatusReason() => null;

        public override bool HasData => DataPollers.Any(x => x.ContainsData);

        public override List<Node> AllNodes { get; } = new List<Node>();
    }
}