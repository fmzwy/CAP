﻿using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using DotNetCore.CAP.Dashboard.Monitoring;
using DotNetCore.CAP.NodeDiscovery;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.CAP.Dashboard
{
    public abstract class RazorPage
    {
        private readonly StringBuilder _content = new StringBuilder();
        private string _body;
        private Lazy<StatisticsDto> _statisticsLazy;

        protected RazorPage()
        {
            GenerationTime = Stopwatch.StartNew();
            Html = new HtmlHelper(this);
        }

        public RazorPage Layout { get; protected set; }
        public HtmlHelper Html { get; }
        public UrlHelper Url { get; private set; }

        public IStorage Storage { get; internal set; }
        public string AppPath { get; internal set; }
        public int StatsPollingInterval { get; internal set; }
        public Stopwatch GenerationTime { get; private set; }

        public StatisticsDto Statistics
        {
            get
            {
                if (_statisticsLazy == null) throw new InvalidOperationException("Page is not initialized.");
                return _statisticsLazy.Value;
            }
        }

        protected DashboardRequest Request { private get; set; }
        protected DashboardResponse Response { private get; set; }
        internal IServiceProvider RequestServices { get; private set; }

        public string RequestPath => Request.Path;

        /// <exclude />
        public abstract void Execute();

        public string Query(string key)
        {
            return Request.GetQuery(key);
        }

        public override string ToString()
        {
            return TransformText(null);
        }

        /// <exclude />
        public void Assign(RazorPage parentPage)
        {
            Request = parentPage.Request;
            Response = parentPage.Response;
            Storage = parentPage.Storage;
            AppPath = parentPage.AppPath;
            StatsPollingInterval = parentPage.StatsPollingInterval;
            Url = parentPage.Url;
            RequestServices = parentPage.RequestServices;

            GenerationTime = parentPage.GenerationTime;
            _statisticsLazy = parentPage._statisticsLazy;
        }

        internal void Assign(DashboardContext context)
        {
            Request = context.Request;
            Response = context.Response;
            RequestServices = context.RequestServices;
            Storage = context.Storage;
            AppPath = context.Options.AppPath;
            StatsPollingInterval = context.Options.StatsPollingInterval;
            Url = new UrlHelper(context);

            _statisticsLazy = new Lazy<StatisticsDto>(() =>
            {
                var monitoring = Storage.GetMonitoringApi();
                var dto = monitoring.GetStatistics();

                SetServersCount(dto);

                return dto;
            });
        }

        private void SetServersCount(StatisticsDto dto)
        {
            if (CapCache.Global.TryGet("cap.nodes.count", out var count))
            {
                dto.Servers = (int) count;
            }
            else
            {
                if (RequestServices.GetService<DiscoveryOptions>() != null)
                {
                    var discoveryProvider = RequestServices.GetService<INodeDiscoveryProvider>();
                    var nodes = discoveryProvider.GetNodes().GetAwaiter().GetResult();
                    dto.Servers = nodes.Count;
                }
            }
        }

        /// <exclude />
        protected void WriteLiteral(string textToAppend)
        {
            if (string.IsNullOrEmpty(textToAppend))
                return;
            _content.Append(textToAppend);
        }

        /// <exclude />
        protected virtual void Write(object value)
        {
            if (value == null)
                return;
            var html = value as NonEscapedString;
            WriteLiteral(html?.ToString() ?? Encode(value.ToString()));
        }

        protected virtual object RenderBody()
        {
            return new NonEscapedString(_body);
        }

        private string TransformText(string body)
        {
            _body = body;

            Execute();

            if (Layout != null)
            {
                Layout.Assign(this);
                return Layout.TransformText(_content.ToString());
            }

            return _content.ToString();
        }

        private static string Encode(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : WebUtility.HtmlEncode(text);
        }
    }
}