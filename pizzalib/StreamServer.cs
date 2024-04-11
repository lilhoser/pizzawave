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
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public class StreamServer : IDisposable
    {
        private CancellationTokenSource CancelSource;
        private Func<WavStreamData, Task> NewCallDataCallback;
        private Settings m_Settings;
        private bool m_Started;
        private bool m_Disposed;

        public StreamServer(
            Func<WavStreamData, Task> NewCallDataCallback_,
            Settings Settings)
        {
            NewCallDataCallback = NewCallDataCallback_;
            CancelSource = new CancellationTokenSource();
            m_Settings = Settings;
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
            CancelSource.Cancel();
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
            CancelSource = new CancellationTokenSource();

            try
            {
                var listenStr = $"Listening on port {m_Settings.listenPort}";
                m_Settings.UpdateConnectionLabelCallback?.Invoke(listenStr);
                Trace(TraceLoggerType.StreamServer, TraceEventType.Information, listenStr);
                m_Started = true;
                listener.Start();
                while (!CancelSource.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(CancelSource.Token);
                    var task = Task.Run(async () => await HandleNewClient(client));
                    tasks.Add(task);
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
                Task.WaitAll(tasks.ToArray());
                listener.Stop();
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
                CancelSource.Cancel();
            }
            else
            {
                CancelSource.CancelAsync();
            }
        }

        private async Task<bool> HandleNewClient(TcpClient Client)
        {
            var clientEndpoint = Client.Client.RemoteEndPoint as IPEndPoint;
            var clientStr = $"{clientEndpoint!.Address}:{clientEndpoint!.Port}";
            Trace(TraceLoggerType.StreamServer,
                  TraceEventType.Verbose,
                  $"Receiving from {clientStr}");
            m_Settings.UpdateConnectionLabelCallback?.Invoke($"Receiving from {clientStr}");
            try
            {
                using (var stream = Client.GetStream())
                {
                    var wavStream = new WavStreamData(m_Settings);
                    var result = await wavStream.ProcessClientData(stream, CancelSource);
                    if (result)
                    {
                        _ = NewCallDataCallback(wavStream); // blocking
                    }
                }
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.StreamServer,
                      TraceEventType.Error,
                      $"HandleNewClient() exception: {ex.Message}");
            }
            return true;
        }
    }
}
