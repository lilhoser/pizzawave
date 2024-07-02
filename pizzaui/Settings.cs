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
    public class Settings : pizzalib.Settings, IEquatable<Settings>
    {
        public MainWindow.CallDisplayGrouping GroupingStrategy;
        public bool ShowAlertMatchesOnly;

        public Settings() : base()
        {
            GroupingStrategy = MainWindow.CallDisplayGrouping.Category;
            ShowAlertMatchesOnly = false;
        }

        public override bool Equals(object? Other)
        {
            if (Other == null)
            {
                return false;
            }
            var field = Other as Settings;
            return Equals(field);
        }

        public bool Equals(Settings? Other)
        {
            if (Other == null)
            {
                return false;
            }
            if (GroupingStrategy != Other.GroupingStrategy ||
                ShowAlertMatchesOnly != Other.ShowAlertMatchesOnly)
            {
                return false;
            }
            return base.Equals(Other);
        }

        public static bool operator ==(Settings? Settings1, Settings? Settings2)
        {
            if ((object)Settings1 == null || (object)Settings2 == null)
                return Equals(Settings1, Settings2);
            return Settings1.Equals(Settings2);
        }

        public static bool operator !=(Settings? Settings1, Settings? Settings2)
        {
            if ((object)Settings1 == null || (object)Settings2 == null)
                return !Equals(Settings1, Settings2);
            return !(Settings1.Equals(Settings2));
        }

        public override int GetHashCode()
        {
            return (GroupingStrategy, ShowAlertMatchesOnly).GetHashCode() + base.GetHashCode();
        }

        public override void Validate()
        {
            base.Validate();
        }
    }
}

