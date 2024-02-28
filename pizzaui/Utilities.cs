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
using System.Diagnostics;
using System.Text;

namespace pizzaui
{
    internal static class Utilities
    {

        public static void LaunchFile(string FileName)
        {
            if (!File.Exists(FileName))
            {
                return;
            }
            var psi = new ProcessStartInfo();
            psi.FileName = FileName;
            psi.UseShellExecute = true;
            Process.Start(psi);
        }

        public static void LaunchBrowser(string Url)
        {
            var psi = new ProcessStartInfo(Url);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }

        public static string Wordwrap(string LongString, int MaxLineLength)
        {
            if (LongString.Length < MaxLineLength)
            {
                return LongString;
            }
            var lineLength = (int)Math.Sqrt((double)LongString.Length) * 2;
            var sb = new StringBuilder();
            var currentLinePosition = 0;
            for (int textIndex = 0; textIndex < LongString.Length; textIndex++)
            {
                if (currentLinePosition >= lineLength && char.IsWhiteSpace(LongString[textIndex]))
                {
                    sb.Append(Environment.NewLine);
                    currentLinePosition = 0;
                }
                if (currentLinePosition == 0)
                {
                    while (textIndex < LongString.Length && char.IsWhiteSpace(LongString[textIndex]))
                    {
                        textIndex++;
                    }
                }

                if (textIndex < LongString.Length)
                {
                    sb.Append(LongString[textIndex]);
                }
                currentLinePosition++;
            }
            return sb.ToString();
        }
    }
}
