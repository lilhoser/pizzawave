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
namespace pizzaui
{
    internal static class Program
    {

        static async Task<int> Main(string[] HeadlessModeArgs)
        {
            if (HeadlessModeArgs.Length > 0)
            {
                var headless = new HeadlessMode();
                return await headless.Run(HeadlessModeArgs);
            }
            else
            {
                //
                // We can't mark `Main` as `STAThread` because it has `async` attribute,
                // which is required to run in headless/console mode.
                //
                // Start a new thread with a static non-async routine properly marked.
                //
                var thread = new Thread(MainStaThread);
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                return 0;
            }
        }

        [STAThread]
        static void MainStaThread()
        {
            //
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            //
            ApplicationConfiguration.Initialize();
            Application.Run(new MainWindow());
        }
    }
}