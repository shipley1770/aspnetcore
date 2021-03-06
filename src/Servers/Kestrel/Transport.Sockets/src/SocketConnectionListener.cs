// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets
{
    internal sealed class SocketConnectionListener : IConnectionListener
    {
        private readonly MemoryPool<byte> _memoryPool;
        private readonly int _settingsCount;
        private readonly Settings[] _settings;
        private readonly ISocketsTrace _trace;
        private Socket? _listenSocket;
        private int _settingsIndex;
        private readonly SocketTransportOptions _options;
        private SafeSocketHandle? _socketHandle;

        public EndPoint EndPoint { get; private set; }

        internal SocketConnectionListener(
            EndPoint endpoint,
            SocketTransportOptions options,
            ISocketsTrace trace)
        {
            EndPoint = endpoint;
            _trace = trace;
            _options = options;
            _memoryPool = _options.MemoryPoolFactory();
            var ioQueueCount = options.IOQueueCount;

            var maxReadBufferSize = _options.MaxReadBufferSize ?? 0;
            var maxWriteBufferSize = _options.MaxWriteBufferSize ?? 0;
            var applicationScheduler = options.UnsafePreferInlineScheduling ? PipeScheduler.Inline : PipeScheduler.ThreadPool;

            if (ioQueueCount > 0)
            {
                _settingsCount = ioQueueCount;
                _settings = new Settings[_settingsCount];

                for (var i = 0; i < _settingsCount; i++)
                {
                    var transportScheduler = options.UnsafePreferInlineScheduling ? PipeScheduler.Inline : new IOQueue();

                    _settings[i] = new Settings
                    {
                        Scheduler = transportScheduler,
                        InputOptions = new PipeOptions(_memoryPool, applicationScheduler, transportScheduler, maxReadBufferSize, maxReadBufferSize / 2, useSynchronizationContext: false),
                        OutputOptions = new PipeOptions(_memoryPool, transportScheduler, applicationScheduler, maxWriteBufferSize, maxWriteBufferSize / 2, useSynchronizationContext: false)
                    };
                }
            }
            else
            {
                var transportScheduler = options.UnsafePreferInlineScheduling ? PipeScheduler.Inline : PipeScheduler.ThreadPool;

                var directScheduler = new Settings[]
                {
                    new Settings
                    {
                        Scheduler = transportScheduler,
                        InputOptions = new PipeOptions(_memoryPool, applicationScheduler, transportScheduler, maxReadBufferSize, maxReadBufferSize / 2, useSynchronizationContext: false),
                        OutputOptions = new PipeOptions(_memoryPool, transportScheduler, applicationScheduler, maxWriteBufferSize, maxWriteBufferSize / 2, useSynchronizationContext: false)
                    }
                };

                _settingsCount = directScheduler.Length;
                _settings = directScheduler;
            }
        }

        internal void Bind()
        {
            if (_listenSocket != null)
            {
                throw new InvalidOperationException(SocketsStrings.TransportAlreadyBound);
            }

            Socket listenSocket;

            switch (EndPoint)
            {
                case FileHandleEndPoint fileHandle:
                    _socketHandle = new SafeSocketHandle((IntPtr)fileHandle.FileHandle, ownsHandle: true);
                    listenSocket = new Socket(_socketHandle);
                    break;
                case UnixDomainSocketEndPoint unix:
                    listenSocket = new Socket(unix.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
                    BindSocket();
                    break;
                case IPEndPoint ip:
                    listenSocket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                    // Kestrel expects IPv6Any to bind to both IPv6 and IPv4
                    if (ip.Address == IPAddress.IPv6Any)
                    {
                        listenSocket.DualMode = true;
                    }
                    BindSocket();
                    break;
                default:
                    listenSocket = new Socket(EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    BindSocket();
                    break;
            }

            void BindSocket()
            {
                try
                {
                    listenSocket.Bind(EndPoint);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    throw new AddressInUseException(e.Message, e);
                }
            }

            Debug.Assert(listenSocket.LocalEndPoint != null);
            EndPoint = listenSocket.LocalEndPoint;

            listenSocket.Listen(_options.Backlog);

            _listenSocket = listenSocket;
        }

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                try
                {
                    Debug.Assert(_listenSocket != null, "Bind must be called first.");

                    var acceptSocket = await _listenSocket.AcceptAsync();

                    // Only apply no delay to Tcp based endpoints
                    if (acceptSocket.LocalEndPoint is IPEndPoint)
                    {
                        acceptSocket.NoDelay = _options.NoDelay;
                    }

                    var setting = _settings[_settingsIndex];

                    var connection = new SocketConnection(acceptSocket,
                        _memoryPool,
                        setting.Scheduler,
                        _trace,
                        setting.InputOptions,
                        setting.OutputOptions,
                        waitForData: _options.WaitForDataBeforeAllocatingBuffer);

                    connection.Start();

                    _settingsIndex = (_settingsIndex + 1) % _settingsCount;

                    return connection;
                }
                catch (ObjectDisposedException)
                {
                    // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                    return null;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted)
                {
                    // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                    return null;
                }
                catch (SocketException)
                {
                    // The connection got reset while it was in the backlog, so we try again.
                    _trace.ConnectionReset(connectionId: "(null)");
                }
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            _listenSocket?.Dispose();

            _socketHandle?.Dispose();
            return default;
        }

        public ValueTask DisposeAsync()
        {
            _listenSocket?.Dispose();

            _socketHandle?.Dispose();

            // Dispose the memory pool
            _memoryPool.Dispose();
            return default;
        }

        private class Settings
        {
            public PipeScheduler Scheduler { get; init; } = default!;
            public PipeOptions InputOptions { get; init; } = default!;
            public PipeOptions OutputOptions { get; init; } = default!;
        }
    }
}
