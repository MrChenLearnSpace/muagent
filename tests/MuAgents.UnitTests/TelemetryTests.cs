using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Tools;

namespace MuAgents.UnitTests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task ToolGatewayEmitsInvocationAndFailureMetrics()
    {
        var measurements = new ConcurrentBag<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MuAgentsTelemetry.SourceName &&
                instrument.Name is "muagents.tool.invocations" or "muagents.tool.failures")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "tool" && Equals(tag.Value, "telemetry.missing"))
                {
                    measurements.Add((instrument.Name, value));
                    break;
                }
            }
        });
        listener.Start();

        var gateway = new ToolGateway(
            [], Options.Create(new ToolGatewayOptions()), NullLogger<ToolGateway>.Instance);
        var result = await gateway.InvokeAsync(
            [new ToolInvocation("call", "telemetry.missing", "{}")],
            new ToolExecutionContext("tenant", "conversation"));

        Assert.True(result[0].Result.IsError);
        Assert.Contains(measurements, item => item is ("muagents.tool.invocations", 1));
        Assert.Contains(measurements, item => item is ("muagents.tool.failures", 1));
    }
}
