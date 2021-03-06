﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics;
using MQTTnet.Exceptions;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server
{
    public sealed class MqttClientPendingMessagesQueue : IDisposable
    {
        private readonly BlockingCollection<MqttBasePacket> _queue = new BlockingCollection<MqttBasePacket>();
        private readonly IMqttServerOptions _options;
        private readonly MqttClientSession _session;
        private readonly IMqttNetLogger _logger;

        public MqttClientPendingMessagesQueue(IMqttServerOptions options, MqttClientSession session, IMqttNetLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public void Start(IMqttChannelAdapter adapter, CancellationToken cancellationToken)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Task.Run(async () => await SendQueuedPacketsAsync(adapter, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        public void Enqueue(MqttBasePacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            _queue.Add(packet);
            _logger.Trace<MqttClientPendingMessagesQueue>("Enqueued packet (ClientId: {0}).", _session.ClientId);
        }

        private async Task SendQueuedPacketsAsync(IMqttChannelAdapter adapter, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await SendQueuedPacketAsync(adapter, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error<MqttClientPendingMessagesQueue>(exception, "Unhandled exception while sending enqueued packet (ClientId: {0}).", _session.ClientId);
            }
        }

        private async Task SendQueuedPacketAsync(IMqttChannelAdapter adapter, CancellationToken cancellationToken)
        {
            MqttBasePacket packet = null;
            try
            {
                packet = _queue.Take(cancellationToken);
                await adapter.SendPacketsAsync(_options.DefaultCommunicationTimeout, cancellationToken, packet).ConfigureAwait(false);

                _logger.Trace<MqttClientPendingMessagesQueue>("Enqueued packet sent (ClientId: {0}).", _session.ClientId);
            }
            catch (Exception exception)
            {
                if (exception is MqttCommunicationTimedOutException)
                {
                    _logger.Warning<MqttClientPendingMessagesQueue>(exception, "Sending publish packet failed due to timeout (ClientId: {0}).", _session.ClientId);
                }
                else if (exception is MqttCommunicationException)
                {
                    _logger.Warning<MqttClientPendingMessagesQueue>(exception, "Sending publish packet failed due to communication exception (ClientId: {0}).", _session.ClientId);
                }
                else if (exception is OperationCanceledException)
                {
                }
                else
                {
                    _logger.Error<MqttClientPendingMessagesQueue>(exception, "Sending publish packet failed (ClientId: {0}).", _session.ClientId);
                }

                if (packet is MqttPublishPacket publishPacket)
                {
                    if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                    {
                        publishPacket.Dup = true;
                        _queue.Add(packet, CancellationToken.None);
                    }
                }

                await _session.StopAsync();
            }
        }

        public void Dispose()
        {
            _queue?.Dispose();
        }
    }
}
