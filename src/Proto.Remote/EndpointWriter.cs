﻿// -----------------------------------------------------------------------
//  <copyright file="EndpointWriter.cs" company="Asynkron HB">
//      Copyright (C) 2015-2017 Asynkron HB All rights reserved
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Proto.Remote
{
    public class EndpointWriter : IActor
    {
        private readonly ILogger _logger = Log.CreateLogger<EndpointWriter>();

        private readonly string _address;
        private readonly IEnumerable<ChannelOption> _channelOptions;
        private Channel _channel;
        private Remoting.RemotingClient _client;
        private AsyncDuplexStreamingCall<MessageBatch, Unit> _stream;
        private IClientStreamWriter<MessageBatch> _streamWriter;
        private readonly CallOptions _callOptions;
        private readonly ChannelCredentials _channelCredentials;

        public EndpointWriter(string address, IEnumerable<ChannelOption> channelOptions, CallOptions callOptions, ChannelCredentials channelCredentials)
        {
            _address = address;
            _channelOptions = channelOptions;
            _callOptions = callOptions;
            _channelCredentials = channelCredentials;
        }

        public async Task ReceiveAsync(IContext context)
        {
            switch (context.Message)
            {
                case Started _:
                    await StartedAsync();
                    break;
                case Stopped _:
                    await StoppedAsync();
                    break;
                case Restarting _:
                    await RestartingAsync();
                    break;
                case IEnumerable<RemoteDeliver> m:
                    var envelopes = new List<MessageEnvelope>();
                    var typeNames = new Dictionary<string,int>();
                    var targetNames = new Dictionary<string,int>();
                    var typeNameList = new List<string>();
                    var targetNameList = new List<string>();
                    foreach(var rd in m)
                    {
                        var targetName = rd.Target.Id;
                        if (!targetNames.TryGetValue(targetName, out var targetId))
                        {
                            targetId = targetNames[targetName] = targetNames.Count;
                            targetNameList.Add(targetName);
                        }

                        var typeName = Serialization.GetTypeName(rd.Message, rd.SerializerId);
                        if (!typeNames.TryGetValue(typeName, out var typeId))
                        {
                            typeId = typeNames[typeName] = typeNames.Count;
                            typeNameList.Add(typeName);
                        }

                        var bytes = Serialization.Serialize(rd.Message,rd.SerializerId);
                        var envelope = new MessageEnvelope
                        {
                            MessageData = bytes,
                            Sender = rd.Sender,
                            Target = targetId,
                            TypeId = typeId,
                            SerializerId = rd.SerializerId,
                        };
                        envelopes.Add(envelope);
                    }

                    var batch = new MessageBatch();
                    batch.TargetNames.AddRange(targetNameList);
                    batch.TypeNames.AddRange(typeNameList);
                    batch.Envelopes.AddRange(envelopes);

                    await SendEnvelopesAsync(batch, context);
                    break;
            }
        }

        private async Task SendEnvelopesAsync(MessageBatch batch, IContext context)
        {
            try
            {
                await _streamWriter.WriteAsync(batch);
            }
            catch (Exception x)
            {
                context.Stash();
                _logger.LogError($"gRPC Failed to send to address {_address}, reason {x.Message}");
                throw;
            }
        }

        //shutdown channel before restarting
        private Task RestartingAsync() => _channel.ShutdownAsync();

        //shutdown channel before stopping
        private Task StoppedAsync() => _channel.ShutdownAsync();

        private Task StartedAsync()
        {
            _logger.LogDebug($"Connecting to address {_address}");
            _channel = new Channel(_address, _channelCredentials, _channelOptions);
            _client = new Remoting.RemotingClient(_channel);
            _stream = _client.Receive(_callOptions);

            Task.Factory.StartNew(async () =>
            {
                try
                {
                    await _stream.ResponseStream.ForEachAsync(i => Actor.Done);
                }
                catch (Exception x)
                {
                    _logger.LogError($"Lost connection to address {_address}, reason {x.Message}");
                    var terminated = new EndpointTerminatedEvent
                    {
                        Address = _address
                    };
                    Actor.EventStream.Publish(terminated);
                }
            });

            _streamWriter = _stream.RequestStream;

            _logger.LogDebug($"Connected to address {_address}");
            return Actor.Done;
        }
    }
}