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
namespace pizzalib
{
    public class AlertEvent
    {
        public Guid AlertId { get; set; }
        public Guid Id { get; set; }
        public DateTime LastTriggered { get; set; }
        public int TriggerCount;
        public int TriggerCountLastInterval;
        private readonly ReaderWriterLockSlim Lock;

        //
        // This "interval" is every 5 seconds. Meant to catch spammy alerts.
        // So the logical limit is 1 alert/email every 5 seconds, PER alert rule.
        //
        public static readonly int s_RealtimeIntervalSec = 5;
        public static readonly int s_RealtimeThresholdPerInterval = 1;

        public AlertEvent(Guid AlertId_)
        {
            AlertId = AlertId_;
            Id = Guid.NewGuid();
            LastTriggered = DateTime.MinValue;
            TriggerCount = 0;
            TriggerCountLastInterval = 0;
            Lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        public void LockExclusive()
        {
            Lock.EnterWriteLock();
        }

        public void LockShared()
        {
            Lock.EnterReadLock();
        }

        public void Unlock()
        {
            if (Lock.IsWriteLockHeld)
            {
                Lock.ExitWriteLock();
            }
            else if (Lock.IsReadLockHeld)
            {
                Lock.ExitReadLock();
            }
        }
    }
}
