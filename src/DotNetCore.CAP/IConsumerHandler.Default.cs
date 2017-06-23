﻿using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.CAP.Abstractions;
using DotNetCore.CAP.Infrastructure;
using DotNetCore.CAP.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetCore.CAP
{
    public class ConsumerHandler : IConsumerHandler, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConsumerInvokerFactory _consumerInvokerFactory;
        private readonly IConsumerClientFactory _consumerClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        private readonly MethodMatcherCache _selector;
        private readonly ConsistencyOptions _options;
        private readonly ICapMessageStore _messageStore;
        private readonly CancellationTokenSource _cts;

        public event EventHandler<ConsistencyMessage> MessageReceieved;

        private TopicContext _context;
        private Task _compositeTask;
        private bool _disposed;

        public ConsumerHandler(
            IServiceProvider serviceProvider,
            IConsumerInvokerFactory consumerInvokerFactory,
            IConsumerClientFactory consumerClientFactory,
            ILoggerFactory loggerFactory,
            ICapMessageStore messageStore,
            MethodMatcherCache selector,
            IOptions<ConsistencyOptions> options) {
            _selector = selector;
            _logger = loggerFactory.CreateLogger<ConsumerHandler>();
            _loggerFactory = loggerFactory;
            _serviceProvider = serviceProvider;
            _consumerInvokerFactory = consumerInvokerFactory;
            _consumerClientFactory = consumerClientFactory;
            _options = options.Value;
            _messageStore = messageStore;
            _cts = new CancellationTokenSource();
        }

        protected virtual void OnMessageReceieved(ConsistencyMessage message) {
            MessageReceieved?.Invoke(this, message);
        }

        public void Start() {
            _context = new TopicContext(_serviceProvider, _cts.Token);

            var matchs = _selector.GetCandidatesMethods(_context);

            var groupingMatchs = matchs.GroupBy(x => x.Value.Attribute.GroupOrExchange);

            foreach (var matchGroup in groupingMatchs) {
                Task.Factory.StartNew(() => {
                    using (var client = _consumerClientFactory.Create(matchGroup.Key, _options.BrokerUrlList)) {
                        client.MessageReceieved += OnMessageReceieved;

                        foreach (var item in matchGroup) {
                            client.Subscribe(item.Key);
                        }

                        client.Listening(TimeSpan.FromSeconds(1));
                    }
                }, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
            }
            _compositeTask = Task.CompletedTask;
        }

        public virtual void OnMessageReceieved(object sender, DeliverMessage message) {
            var consistencyMessage = new ConsistencyMessage() {
                Topic = message.MessageKey,
                Payload = "Reveived:" + Encoding.UTF8.GetString(message.Body),
                Status = MessageStatus.Received
            };

            _logger.LogInformation("message receieved message topic name: " + consistencyMessage.Id);

            _messageStore.CreateAsync(consistencyMessage, _cts.Token).Wait();

            try {
                var executeDescriptor = _selector.GetTopicExector(message.MessageKey);

                var consumerContext = new ConsumerContext(executeDescriptor, message);

                var invoker = _consumerInvokerFactory.CreateInvoker(consumerContext);

                invoker.InvokeAsync();

                _messageStore.UpdateAsync(consistencyMessage, _cts.Token).Wait();
            }
            catch (Exception ex) {
                _logger.LogError("exception raised when excute method : " + ex.Message);
            }
        }

        public void Dispose() {
            if (_disposed) {
                return;
            }
            _disposed = true;

            _logger.ServerShuttingDown();
            _cts.Cancel();

            try {
                _compositeTask.Wait((int)TimeSpan.FromSeconds(60).TotalMilliseconds);
            }
            catch (AggregateException ex) {
                var innerEx = ex.InnerExceptions[0];
                if (!(innerEx is OperationCanceledException)) {
                    _logger.ExpectedOperationCanceledException(innerEx);
                }
            }
        }
    }
}