using System.Text.Json.Nodes;

namespace pizzad.Tests;

public sealed class TrRecorderCapacitySizerTests
{
    [Fact]
    public void EnsureJsonConfigRecorderCapacity_SizesLiveAirspyR2Plan()
    {
        var root = JsonNode.Parse("""
            {
              "sources": [
                {
                  "device": "airspy=637862DC2E457DD7",
                  "center": 854743750,
                  "rate": 10000000,
                  "digitalRecorders": 4
                }
              ],
              "systems": [
                {
                  "shortName": "chattanooga-simulcast-hamilton-t",
                  "control_channels": [855212500],
                  "channels": [
                    854387500, 854837500, 855987500, 856087500, 856762500, 857237500, 857437500,
                    857987500, 858437500, 858687500, 858937500, 859187500, 859287500, 859387500
                  ]
                },
                {
                  "shortName": "cleveland-bradley-tn",
                  "control_channels": [851050000],
                  "channels": [851550000, 852050000, 852550000, 853050000, 853550000]
                }
              ]
            }
            """)!.AsObject();
        var changes = new List<string>();

        TrRecorderCapacitySizer.EnsureJsonConfigRecorderCapacity(root, changes);

        Assert.Equal(24, root["sources"]![0]!["digitalRecorders"]!.GetValue<int>());
        Assert.Contains(changes, change => change.Contains("digitalRecorders: 4 -> 24", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureJsonConfigRecorderCapacity_PreservesHigherManualCount()
    {
        var root = JsonNode.Parse("""
            {
              "sources": [
                {
                  "center": 854743750,
                  "rate": 10000000,
                  "digitalRecorders": 30
                }
              ],
              "systems": [
                {
                  "shortName": "site-a",
                  "control_channels": [855212500],
                  "channels": [855987500, 856087500]
                }
              ]
            }
            """)!.AsObject();
        var changes = new List<string>();

        TrRecorderCapacitySizer.EnsureJsonConfigRecorderCapacity(root, changes);

        Assert.Equal(30, root["sources"]![0]!["digitalRecorders"]!.GetValue<int>());
        Assert.Empty(changes);
    }

    [Fact]
    public void EstimateForSetupSource_UsesCoveredNonControlFrequencies()
    {
        var source = new SetupTrConfigSourceDto(
            "source-1",
            "637862DC2E457DD7",
            "Airspy",
            "osmosdr",
            "airspy=637862DC2E457DD7",
            854743750,
            10000000,
            "15",
            "",
            [851.05, 851.55, 852.05, 855.2125, 855.9875, 856.0875],
            []);
        var systems = new[]
        {
            new SetupTrConfigSystemDto("System", "cleveland", "Cleveland", [851.05, 851.55, 852.05], [851.05], 0, "", ""),
            new SetupTrConfigSystemDto("System", "chattanooga", "Chattanooga", [855.2125, 855.9875, 856.0875], [855.2125], 0, "", "")
        };

        var estimate = TrRecorderCapacitySizer.EstimateForSetupSource(source, systems);

        Assert.Equal(8, estimate);
    }
}
