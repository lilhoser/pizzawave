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
using pizzalib;
using System.Diagnostics;
using System.Reflection;

namespace pizzacmd
{
    using static TraceLogger;

    internal class PizzaCmd : StandaloneClient
    {
        public PizzaCmd()
        {
            m_CallManager = new LiveCallManager(NewCallTranscribed);
        }

        protected override void PrintUsage(string Error)
        {
            Console.WriteLine(Error);
            Console.WriteLine("Usage: pizzacmd.exe --settings=<path> [--talkgroups=<path>]");
        }

        protected override void WriteBanner()
        {
            base.WriteBanner();

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Trace(TraceLoggerType.Main, TraceEventType.Warning, $"pizzacmd {version}");
        }

        protected override void NewCallTranscribed(TranscribedCall Call)
        {
            Trace(TraceLoggerType.Main, TraceEventType.Information, $"{Call.ToString(m_Settings!)}");
        }

        public override async Task<int> Run(string[] Args)
        {
            TraceLogger.Initialize(true);
            pizzalib.TraceLogger.Initialize(true);
            var result = await base.Run(Args.ToArray());
            TraceLogger.Shutdown();
            pizzalib.TraceLogger.Shutdown();
            return result;
        }
    }
}
