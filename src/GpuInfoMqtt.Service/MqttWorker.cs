using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;

namespace GpuInfoMqtt;

public sealed partial class MqttWorker(SettingsStore store, StatusTracker status, GpuReader reader, ILogger<MqttWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private sealed record SensorDef(
        string Key, string Name, string Unit, string? DeviceClass, string? Icon, int Precision, bool Diagnostic = false);

    private static readonly SensorDef[] Sensors =
    [
        new("power", "Power", "W", "power", null, 1),
        new("power_limit", "Power limit", "W", "power", null, 0, Diagnostic: true),
        new("memory_used", "Memory used", "MiB", "data_size", "mdi:memory", 0),
        new("memory_total", "Memory total", "MiB", "data_size", "mdi:memory", 0, Diagnostic: true),
        new("memory_percent", "Memory usage", "%", null, "mdi:memory", 1),
        new("temperature", "Temperature", "°C", "temperature", null, 0),
        new("fan", "Fan speed", "%", null, "mdi:fan", 0),
        new("utilization", "GPU utilization", "%", null, "mdi:gauge", 0),
    ];

    private readonly MqttFactory _factory = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var changed = store.ChangeToken;
            var s = store.Current;
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stop, changed);

            if (!s.Enabled || string.IsNullOrWhiteSpace(s.Host))
            {
                status.Set("disabled", s.Enabled ? "Broker host is not configured." : "Publishing is disabled.");
                await Delay(Timeout.InfiniteTimeSpan, cycle.Token);
                continue;
            }

            using (var client = _factory.CreateMqttClient())
            {
                try
                {
                    await RunSessionAsync(client, s, cycle.Token);
                }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested)
                {
                    // settings changed or service stopping
                }
                catch (Exception ex)
                {
                    var msg = Describe(ex);
                    status.Set("error", msg);
                    status.Event("error", msg);
                    log.LogWarning("MQTT session failed: {Message}", msg);
                }
                finally
                {
                    await GoOfflineAsync(client, s);
                }
            }

            if (changed.IsCancellationRequested && !stop.IsCancellationRequested)
                status.Event("info", "Settings changed, reconnecting.");
            else
                await Delay(RetryDelay, cycle.Token);
        }
    }

    private async Task RunSessionAsync(IMqttClient client, AppSettings s, CancellationToken ct)
    {
        var host = Slug(s.DeviceName);
        var availabilityTopic = $"{s.BaseTopic}/{host}/status";
        var haStatusTopic = $"{s.DiscoveryPrefix}/status";
        var republish = 0;

        client.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Topic == haStatusTopic && e.ApplicationMessage.ConvertPayloadToString() == "online")
            {
                Interlocked.Exchange(ref republish, 1);
                status.Event("info", "Home Assistant came online; re-sending discovery.");
            }
            return Task.CompletedTask;
        };

        status.Set("connecting", $"Connecting to {s.Host}:{s.Port}…");
        await client.ConnectAsync(BuildOptions(s, availabilityTopic), ct);
        status.Set("connected", $"Connected to {s.Host}:{s.Port}");
        status.Event("info", $"Connected to {s.Host}:{s.Port}");
        log.LogInformation("Connected to MQTT broker {Host}:{Port}", s.Host, s.Port);

        await client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder().WithTopicFilter(haStatusTopic).Build(), ct);
        await PublishAsync(client, availabilityTopic, "online", retain: true, ct);

        var announced = new HashSet<string>();
        string? lastGpuError = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!client.IsConnected) throw new InvalidOperationException("Connection to the broker was lost.");
            if (Interlocked.Exchange(ref republish, 0) == 1) announced.Clear();

            IReadOnlyList<GpuReading> gpus;
            try
            {
                gpus = await reader.ReadAsync(s.NvidiaSmiPath, TimeSpan.FromSeconds(6), ct);
                if (lastGpuError is not null) status.Set("connected", $"Connected to {s.Host}:{s.Port}");
                lastGpuError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var msg = "nvidia-smi failed: " + ex.Message;
                if (msg != lastGpuError)
                {
                    status.Set("error", msg);
                    status.Event("error", msg);
                    log.LogWarning("{Message}", msg);
                }
                lastGpuError = msg;
                gpus = [];
            }

            foreach (var gpu in gpus)
            {
                var gpuId = Slug(gpu.Uuid);
                var stateTopic = $"{s.BaseTopic}/{host}/{gpuId}/state";

                if (announced.Add(gpu.Uuid))
                    await PublishDiscoveryAsync(client, s, gpu, gpus.Count, gpuId, stateTopic, availabilityTopic, ct);

                var state = new JsonObject
                {
                    ["power"] = gpu.Power,
                    ["power_limit"] = gpu.PowerLimit,
                    ["memory_used"] = gpu.MemoryUsed,
                    ["memory_total"] = gpu.MemoryTotal,
                    ["memory_percent"] = gpu.MemoryPercent,
                    ["temperature"] = gpu.Temperature,
                    ["fan"] = gpu.Fan,
                    ["utilization"] = gpu.Utilization,
                };
                await PublishAsync(client, stateTopic, state.ToJsonString(), retain: false, ct);
            }

            if (gpus.Count > 0) status.Published();
            await Task.Delay(TimeSpan.FromSeconds(s.IntervalSeconds), ct);
        }
    }

    private async Task PublishDiscoveryAsync(IMqttClient client, AppSettings s, GpuReading gpu, int gpuCount,
        string gpuId, string stateTopic, string availabilityTopic, CancellationToken ct)
    {
        var shortName = gpu.Name.Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();
        var deviceName = gpuCount > 1 ? $"{s.DeviceName} GPU{gpu.Index} {shortName}" : $"{s.DeviceName} {shortName}";
        var nodeId = $"{Slug(s.BaseTopic)}_{gpuId}";

        foreach (var sensor in Sensors)
        {
            var config = new JsonObject
            {
                ["name"] = sensor.Name,
                ["unique_id"] = $"{nodeId}_{sensor.Key}",
                ["state_topic"] = stateTopic,
                ["value_template"] = $"{{{{ value_json.{sensor.Key} }}}}",
                ["unit_of_measurement"] = sensor.Unit,
                ["state_class"] = "measurement",
                ["suggested_display_precision"] = sensor.Precision,
                ["availability_topic"] = availabilityTopic,
                ["device"] = new JsonObject
                {
                    ["identifiers"] = new JsonArray(nodeId),
                    ["name"] = deviceName,
                    ["manufacturer"] = "NVIDIA",
                    ["model"] = gpu.Name,
                },
                ["origin"] = new JsonObject { ["name"] = "GpuInfoMqtt" },
            };
            if (sensor.DeviceClass is not null) config["device_class"] = sensor.DeviceClass;
            if (sensor.Icon is not null) config["icon"] = sensor.Icon;
            if (sensor.Diagnostic) config["entity_category"] = "diagnostic";

            var topic = $"{s.DiscoveryPrefix}/sensor/{nodeId}/{sensor.Key}/config";
            await PublishAsync(client, topic, config.ToJsonString(), retain: true, ct);
        }

        status.Event("info", $"Announced '{deviceName}' to Home Assistant ({Sensors.Length} sensors).");
    }

    private MqttClientOptions BuildOptions(AppSettings s, string availabilityTopic)
    {
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(s.Host, s.Port)
            .WithClientId($"gpuinfo-{Slug(Environment.MachineName)}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic(availabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(s.Username))
            b.WithCredentials(s.Username, store.GetPassword(s) ?? "");

        if (s.UseTls)
            b.WithTlsOptions(o =>
            {
                o.UseTls();
                if (s.AllowUntrustedCertificates) o.WithCertificateValidationHandler(_ => true);
            });

        return b.Build();
    }

    private static Task PublishAsync(IMqttClient client, string topic, string payload, bool retain, CancellationToken ct) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(retain ? MqttQualityOfServiceLevel.AtLeastOnce : MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), ct);

    private async Task GoOfflineAsync(IMqttClient client, AppSettings s)
    {
        if (!client.IsConnected) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await PublishAsync(client, $"{s.BaseTopic}/{Slug(s.DeviceName)}/status", "offline", retain: true, cts.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), cts.Token);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Graceful disconnect failed");
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    private static string Describe(Exception ex)
    {
        if (ex is MqttCommunicationTimedOutException) return "Timed out while talking to the broker.";
        if (ex is MQTTnet.Adapter.MqttConnectingFailedException cf)
            return $"Broker refused the connection: {cf.ResultCode}";
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var text = inner == ex ? ex.Message : $"{ex.Message} ({inner.Message})";
        return text.Replace("Unspecified/", "");
    }

    public static string Slug(string s) => SlugRegex().Replace(s.ToLowerInvariant(), "_").Trim('_');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
