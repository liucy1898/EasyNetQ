using System;
using System.Threading;
using System.Threading.Tasks;
using EasyNetQ.Events;
using EasyNetQ.Logging;
using EasyNetQ.Topology;
using RabbitMQ.Client;

namespace EasyNetQ.Consumer
{
    internal class AsyncBasicConsumer : AsyncDefaultBasicConsumer, IDisposable
    {
        private readonly ILog logger = LogProvider.For<AsyncBasicConsumer>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly IQueue queue;
        private readonly IEventBus eventBus;
        private readonly IHandlerRunner handlerRunner;
        private readonly MessageHandler messageHandler;

        public AsyncBasicConsumer(
            IModel model,
            IQueue queue,
            IEventBus eventBus,
            IHandlerRunner handlerRunner,
            MessageHandler messageHandler
        ) : base(model)
        {
            this.queue = queue;
            this.eventBus = eventBus;
            this.handlerRunner = handlerRunner;
            this.messageHandler = messageHandler;
        }

        public IQueue Queue => queue;

        public override async Task HandleBasicDeliver(
            string consumerTag,
            ulong deliveryTag,
            bool redelivered,
            string exchange,
            string routingKey,
            IBasicProperties properties,
            ReadOnlyMemory<byte> body
        )
        {
            if (cts.IsCancellationRequested)
                return;

            if (logger.IsDebugEnabled())
            {
                logger.DebugFormat(
                    "Message delivered to consumer {consumerTag} with deliveryTag {deliveryTag}",
                    consumerTag,
                    deliveryTag
                );
            }

            var messageBody = body.ToArray();
            var messageReceivedInfo = new MessageReceivedInfo(
                consumerTag, deliveryTag, redelivered, exchange, routingKey, queue.Name
            );
            var messageProperties = new MessageProperties(properties);
            eventBus.Publish(new DeliveredMessageEvent(messageReceivedInfo, messageProperties, messageBody));

            var context = new ConsumerExecutionContext(messageHandler, messageReceivedInfo, messageProperties, messageBody);
            var ackStrategy = await handlerRunner.InvokeUserMessageHandlerAsync(context, cts.Token).ConfigureAwait(false);
            var ackResult = ackStrategy(Model, deliveryTag);
            eventBus.Publish(new AckEvent(messageReceivedInfo, messageProperties, messageBody, ackResult));
        }

        /// <inheritdoc />
        public override async Task OnCancel(params string[] consumerTags)
        {
            await base.OnCancel(consumerTags).ConfigureAwait(false);
            logger.InfoFormat(
                "Consumer with consumerTags {consumerTags} has cancelled",
                string.Join(", ", consumerTags)
            );
        }

        /// <inheritdoc />
        public void Dispose()
        {
            cts.Dispose();
            eventBus.Publish(new ConsumerModelDisposedEvent(ConsumerTags));
        }
    }
}