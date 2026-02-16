/* 
Licensed to the Apache Software Foundation (ASF) under one
or more contributor license agreements.  See the NOTICE file
distributed with this work for additional information
regarding copyright ownership.  The ASF licenses this file
to you under the Apache License, Version 2.0 (the
"License"); you may not use this file except in compliance
with the License.  You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing,
software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
KIND, either express or implied.  See the License for the
specific language governing permissions and limitations
under the License.
*/
using System.Net.Sockets;
using System.Net;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public class StreamServer : IDisposable
    {
        private CancellationTokenSource CancelSource;
        private Func<RawCallData, Task> NewCallDataCallback;
        private Settings m_Settings;
        private bool m_Started;
        private bool m_Disposed;
        private readonly SemaphoreSlim m_ClientSemaphore;
        private readonly int m_MaxConcurrentClients = 10;

        public StreamServer(
            Func<RawCallData, Task> NewCallDataCallback_,
            Settings Settings)
        {
            NewCallDataCallback = NewCallDataCallback_;
            CancelSource = new CancellationTokenSource();
            m_Settings = Settings;
            m_ClientSemaphore = new SemaphoreSlim(m_MaxConcurrentClients, m_MaxConcurrentClients);
        }

        ~StreamServer()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            Trace(TraceLoggerType.StreamServer, TraceEventType.Information, "Stream server disposed.");

            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_Started = false;
            CancelSource?.Cancel();
            CancelSource?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public bool IsStarted()
        {
            return m_Started;
        }

        public async Task<bool> Listen()
        {
            var ipEndPoint = new IPEndPoint(IPAddress.Any, m_Settings.listenPort);
            TcpListener listener = new(ipEndPoint);
            List<Task> tasks = new List<Task>();
            CancelSource?.Cancel();
            CancelSource?.Dispose();
            CancelSource = new CancellationTokenSource();

            try
            {
                var listenStr = $"Listening on port {m_Settings.listenPort}";
                m_Settings.UpdateConnectionLabelCallback?.Invoke(listenStr);
                Trace(TraceLoggerType.StreamServer, TraceEventType.Information, listenStr);
                // Configure trace level based on platform - Linux/RPI needs less verbose logging
                // to avoid high CPU usage from file I/O and console output
                m_Started = true;
                listener.Start();
                while (!CancelSource.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(CancelSource.Token);
                    // Rate limit: wait for a slot to be available
                    await m_ClientSemaphore.WaitAsync(CancelSource.Token);
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleNewClient(client);
                        }
                        finally
                        {
                            m_ClientSemaphore.Release();
                        }
                    });
                    tasks.Add(task);
                    // Small delay to prevent busy polling on Linux/RPI
                    // This reduces CPU usage when no clients are connecting
                                    }
            }
            catch (AggregateException ae)
            {
                if (ae.InnerException != null && ae.InnerException is OperationCanceledException)
                {
                    Trace(TraceLoggerType.StreamServer,
                          TraceEventType.Information,
                          $"Successfully canceled listener operation.");
                    return true;
                }
                var str = new StringBuilder();
                str.AppendLine(ae.Message);
                if (ae.InnerException != null && !string.IsNullOrEmpty(ae.InnerException.Message))
                {
                    str.AppendLine(ae.InnerException.Message);
                }
                Trace(TraceLoggerType.Settings,
                      TraceEventType.Error,
                      $"Caught aggregate exception: {str}");
            }
            catch (OperationCanceledException)
            {
                Trace(TraceLoggerType.StreamServer,
                      TraceEventType.Information,
                      $"Successfully canceled listener operation.");
            }
            finally
            {
                try
                {
                    if (tasks.Count > 0)
                    {
                        Task.WaitAll(tasks.ToArray());
                    }
                }
                catch (Exception)
                {
                    // tasks may have been cancelled
                }
                listener.Stop();
                tasks.Clear();
                m_Started = false;
            }
            return true;
        }

        public void Shutdown(bool block = false)
        {
            Trace(TraceLoggerType.StreamServer,
                  TraceEventType.Information,
                  $"Received shutdown request.");
            if (block)
            {
                CancelSource?.Cancel();
            }
            else
            {
                CancelSource?.CancelAsync();
            }
        }

        private async Task<bool> HandleNewClient(TcpClient Client)
        {
            var clientEndpoint = Client.Client.RemoteEndPoint as IPEndPoint;
            var clientStr = $"{clientEndpoint!.Address}:{clientEndpoint!.Port}";
            Trace(TraceLoggerType.StreamServer,
                  TraceEventType.Information,
                  $"Connection from {clientStr}");
            m_Settings.UpdateConnectionLabelCallback?.Invoke($"Connection from {clientStr}");
            try
            {
                using (var stream = Client.GetStream())
                {
                    var wavStream = new RawCallData(m_Settings);
                    try
                    {
                        var result = await wavStream.ProcessClientData(stream, CancelSource);
                        if (result && !CancelSource.IsCancellationRequested)
                        {
                            // Await the callback to ensure wavStream isn't disposed prematurely
                            await NewCallDataCallback(wavStream);
                        }
                    }
                    finally
                    {
                        // Dispose after callback completes to release PCM buffer
                        wavStream.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.StreamServer,
                      TraceEventType.Error,
                      $"HandleNewClient() exception: {ex.Message}");
                return false;
            }
            return true;
        }
    }
}
