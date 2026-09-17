using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;
using WireLink.Core.Services;
using WireLink.Infrastructure.Settings;

namespace WireLink.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task Settings_without_device_type_default_to_frame_controller()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirelink-settings-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "PortName": "COM8",
                  "BaudRate": 9600,
                  "DeviceAddress": 1
                }
                """);

            var settings = await new JsonSettingsService(path).LoadAsync();

            Assert.Equal(DeviceType.FrameController, settings.DeviceType);
            Assert.Equal("COM8", settings.PortName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Device_read_keeps_successful_blocks_when_one_block_fails()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == 336) throw new TimeoutException("模拟超时");
            return Enumerable.Range(start, count).Select(x => (ushort)(x == 1552 ? 0x030B : x)).ToArray();
        });
        var service = new DeviceDataService(client, new RegisterParser());
        var result = await service.ReadAsync(1, DeviceType.FrameController, WordOrder.HighWordFirst, BreakerSeries.BW1);
        Assert.Single(result.Errors);
        Assert.Contains(result.Values, x => x.Name == "A 相电压");
        Assert.DoesNotContain(result.Values, x => x.Name == "高精度电流测量 Ia");
        Assert.Contains(result.Values, x => x.Name == "高精度电流测量 Ib");
        Assert.Equal("536", result.Values.Single(x => x.Name == "A 相电流").Value);
    }

    [Fact]
    public async Task Fault_read_writes_selector_then_reads_record_rated_current_and_operation_count()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == 1031)
            {
                Assert.Equal((ushort)1, count);
                return [128];
            }
            if (start == 1552)
            {
                Assert.Equal((ushort)1, count);
                return [0x0304];
            }
            Assert.Equal((ushort)768, start); Assert.Equal((ushort)18, count);
            var raw = CreateFaultRecord();
            return raw;
        });
        var result = await new FaultRecordService(client, new RegisterParser()).ReadAsync(
            1, FaultRecordType.Fault, 3, WordOrder.HighWordFirst, BreakerSeries.BW1, TimeSpan.Zero);
        Assert.Equal(((ushort)785, (ushort)0x0300), client.LastWrite);
        Assert.Empty(result.Errors); Assert.Equal(16, result.Values.Count);
        Assert.Equal("2026-07-22 14:30:09",
            result.Values.Single(x => x.Name == "故障记录时间").Value);
        Assert.Equal("630 A", result.Values.Single(x => x.Name == "额定电流").DisplayValue);
        Assert.Equal("128", result.Values.Single(x => x.Name == "总操作次数").Value);
    }

    [Fact]
    public async Task Fault_timestamp_read_selects_record_and_reads_only_three_time_registers()
    {
        await using var client = new FakeClient((start, count) =>
        {
            Assert.Equal((ushort)768, start);
            Assert.Equal((ushort)3, count);
            return [0x2607, 0x2214, 0x3009];
        });

        var value = await new FaultRecordService(client, new RegisterParser()).ReadTimestampAsync(
            4, FaultRecordType.StateChange, 3, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 7, 22, 14, 30, 9, DateTimeKind.Unspecified), value);
        Assert.Equal(((ushort)785, (ushort)0x0302), client.LastWrite);
        Assert.Equal([(Start: (ushort)768, Count: (ushort)3)], client.ReadRequests);
    }

    [Fact]
    public async Task Waveform_time_source_fault_record_zero_writes_selector_0000()
    {
        await using var client = new FakeClient((start, count) =>
        {
            Assert.Equal((ushort)768, start);
            Assert.Equal((ushort)3, count);
            return [0x2607, 0x2214, 0x3009];
        });

        await new FaultRecordService(client, new RegisterParser()).ReadTimestampAsync(
            4, FaultRecordType.Fault, 0, TimeSpan.Zero);

        Assert.Equal(((ushort)785, (ushort)0x0000), client.LastWrite);
    }

    [Fact]
    public async Task Empty_fault_timestamp_is_rejected_before_waveform_can_start()
    {
        await using var client = new FakeClient((_, _) => [0, 0, 0]);

        var exception = await Assert.ThrowsAsync<FormatException>(() =>
            new FaultRecordService(client, new RegisterParser()).ReadTimestampAsync(
                4, FaultRecordType.Fault, 1, TimeSpan.Zero));

        Assert.Contains("故障记录时间为空", exception.Message);
        Assert.Single(client.ReadRequests);
    }

    [Fact]
    public async Task Fault_timestamp_read_requires_exactly_three_registers()
    {
        await using var client = new FakeClient((_, _) => [0x2607, 0x2214]);

        var exception = await Assert.ThrowsAsync<ModbusProtocolException>(() =>
            new FaultRecordService(client, new RegisterParser()).ReadTimestampAsync(
                4, FaultRecordType.Fault, 1, TimeSpan.Zero));

        Assert.Contains("期望 3，收到 2", exception.Message);
        Assert.Single(client.ReadRequests);
    }

    [Fact]
    public async Task Operation_count_failure_does_not_discard_fault_record()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == 1031) throw new TimeoutException("1031 超时");
            if (start == 1552) return [0x0304];
            return CreateFaultRecord();
        });

        var result = await new FaultRecordService(client, new RegisterParser()).ReadAsync(
            1, FaultRecordType.Fault, 0, WordOrder.HighWordFirst, BreakerSeries.BW1, TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("1031", result.Errors[0]);
        Assert.Contains(result.Values, x => x.Name == "故障记录时间");
        Assert.DoesNotContain(result.Values, x => x.Name == "总操作次数");
    }

    [Fact]
    public async Task Fault_record_failure_does_not_discard_operation_count()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == 768) throw new TimeoutException("故障记录超时");
            if (start == 1552) return [0x0304];
            Assert.Equal((ushort)1031, start);
            return [128];
        });

        var result = await new FaultRecordService(client, new RegisterParser()).ReadAsync(
            1, FaultRecordType.Fault, 0, WordOrder.HighWordFirst, BreakerSeries.BW1, TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("768～785", result.Errors[0]);
        Assert.Equal(2, result.Values.Count);
        Assert.Equal("630 A", result.Values.Single(x => x.Name == "额定电流").DisplayValue);
        Assert.Equal("128", result.Values.Single(x => x.Name == "总操作次数").Value);
    }

    [Fact]
    public async Task Rated_current_failure_does_not_discard_fault_record_or_operation_count()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == 1552) throw new TimeoutException("1552 超时");
            if (start == 1031) return [128];
            return CreateFaultRecord();
        });

        var result = await new FaultRecordService(client, new RegisterParser()).ReadAsync(
            1, FaultRecordType.Fault, 0, WordOrder.HighWordFirst, BreakerSeries.BW1, TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("1552", result.Errors[0]);
        Assert.Contains(result.Values, x => x.Name == "故障记录时间");
        Assert.Contains(result.Values, x => x.Name == "总操作次数");
        Assert.DoesNotContain(result.Values, x => x.Name == "额定电流");
    }

    [Fact]
    public async Task Connection_test_reads_exactly_register_256()
    {
        await using var client = new FakeClient((start, count) => { Assert.Equal((ushort)256, start); Assert.Equal((ushort)1, count); return [230]; });
        Assert.True(await new DeviceDataService(client, new RegisterParser()).TestConnectionAsync(
            1, DeviceType.FrameController));
    }

    [Fact]
    public async Task Molded_case_connection_test_reads_register_0001()
    {
        await using var client = new FakeClient((start, count) =>
        {
            Assert.Equal((ushort)0x0001, start);
            Assert.Equal((ushort)1, count);
            return [120];
        });

        Assert.True(await new DeviceDataService(client, new RegisterParser()).TestConnectionAsync(
            1, DeviceType.MoldedCaseCircuitBreaker));
    }

    [Fact]
    public async Task Molded_case_device_read_uses_two_protocol_blocks()
    {
        await using var client = new FakeClient((start, count) => start switch
        {
            0x0001 => [120, 118, 121, 0, 5, 121, 2],
            0x0032 => [8, 560, 1, 125],
            _ => throw new InvalidOperationException($"非预期读取 {start:X4}H/{count}"),
        });

        var result = await new DeviceDataService(client, new RegisterParser()).ReadAsync(
            1, DeviceType.MoldedCaseCircuitBreaker, WordOrder.HighWordFirst, BreakerSeries.BW1);

        Assert.Empty(result.Errors);
        Assert.Equal([((ushort)0x0001, (ushort)7), ((ushort)0x0032, (ushort)4)],
            client.ReadRequests.Select(request => (request.Start, request.Count)));
        Assert.Equal("C相", result.Values.Single(value => value.Name == "最大电流所在相").Value);
        Assert.Equal("短延时故障", result.Values.Single(value => value.Name == "故障类型记录").Value);
        Assert.Equal("560", result.Values.Single(value => value.Name == "故障最大相电流记录").DisplayValue);
        Assert.Equal("2.50 s", result.Values.Single(value => value.Name == "故障时间记录").DisplayValue);
    }

    [Fact]
    public async Task Molded_case_device_read_keeps_fault_block_when_measurement_block_fails()
    {
        await using var client = new FakeClient((start, _) => (start switch
        {
            0x0001 => throw new TimeoutException("测量区超时"),
            0x0032 => [8, 560, 1, 125],
            _ => throw new InvalidOperationException(),
        }));

        var result = await new DeviceDataService(client, new RegisterParser()).ReadAsync(
            1, DeviceType.MoldedCaseCircuitBreaker, WordOrder.HighWordFirst, BreakerSeries.BW1);

        Assert.Single(result.Errors);
        Assert.DoesNotContain(result.Values, value => value.Name == "A 相电流");
        Assert.Contains(result.Values, value => value.Name == "故障类型记录");
    }

    [Fact]
    public async Task Protection_read_includes_001d_and_returns_raw_decimal_value()
    {
        await using var client = new FakeClient((start, count) => start switch
        {
            0x0016 => [100, 30, 500, 10, 800, 50, 4, 35],
            0x001E => [5, 80],
            _ => throw new InvalidOperationException($"非预期读取 {start:X4}H/{count}"),
        });

        var result = await new ProtectionDataService(client, new RegisterParser()).ReadAsync(
            1, DeviceType.MoldedCaseCircuitBreaker, WordOrder.HighWordFirst, BreakerSeries.BW1);

        Assert.Empty(result.Errors);
        Assert.Equal([((ushort)0x0016, (ushort)8), ((ushort)0x001E, (ushort)2)],
            client.ReadRequests.Select(request => (request.Start, request.Count)));
        Assert.Contains(client.ReadRequests, request =>
            request.Start <= 0x001D && request.Start + request.Count - 1 >= 0x001D);
        var leakageCurrent = result.Values.Single(value => value.Name == "漏电电流");
        Assert.Equal("35", leakageCurrent.DisplayValue);
        Assert.Single(leakageCurrent.RawSamples);
        Assert.Equal((ushort)35, leakageCurrent.RawSamples[0].Value);
        Assert.Equal(ParseStatus.Success, leakageCurrent.Status);
        Assert.Equal("30 s", result.Values.Single(value => value.Name == "长延时时间设定值 T1").DisplayValue);
        Assert.Equal("0.2 s", result.Values.Single(value => value.Name == "短延时时间设定值 T2").DisplayValue);
        Assert.Equal("0.5 s", result.Values.Single(value => value.Name == "接地时间设定值 Tg").DisplayValue);
        Assert.Equal("0.6 s", result.Values.Single(value => value.Name == "预报警时间设定值 Tp").DisplayValue);
    }

    [Fact]
    public async Task Frame_protection_read_uses_only_requested_blocks_and_hidden_dependencies()
    {
        await using var client = new FakeClient((start, count) => start switch
        {
            1552 => [0x0204],
            1793 => [0x0C00],
            1280 => [630],
            1282 => [945, 20, 1260, 6300, 1, 315, 25],
            1296 => [250, 200, 0x141E, 0x141E, 100, 1000],
            _ => throw new InvalidOperationException($"非预期读取 {start}/{count}"),
        });

        var result = await new ProtectionDataService(client, new RegisterParser()).ReadAsync(
            1, DeviceType.FrameController, WordOrder.HighWordFirst, BreakerSeries.BW1);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [((ushort)1552,(ushort)1),((ushort)1793,(ushort)1),((ushort)1280,(ushort)1),
                ((ushort)1282,(ushort)7),((ushort)1296,(ushort)6)],
            client.ReadRequests.Select(request => (request.Start, request.Count)));
        Assert.DoesNotContain(client.ReadRequests, request => request.Start == 1281 || request.Start is >= 1289 and <= 1295);
        Assert.Equal("地电流型", result.Values.Single(value => value.Name == "接地保护方式").DisplayValue);
        Assert.Equal("315 A", result.Values.Single(value => value.Name == "保护动作值").DisplayValue);
        Assert.Equal("0.30 s", result.Values.Single(value => value.Name == "报警启动时间").DisplayValue);
        Assert.Equal("0.20 s", result.Values.Single(value => value.Name == "报警返回时间").DisplayValue);
        Assert.Equal("30%", result.Values.Single(value => value.Name == "I 不平衡启动值").DisplayValue);
        Assert.Equal("20%", result.Values.Single(value => value.Name == "I 不平衡返回值").DisplayValue);
    }

    [Fact]
    public async Task Waveform_read_requests_18_blocks_and_builds_aligned_points()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == RegisterCatalog.RatedCurrentRegisterAddress) return [0x0204];
            var block = WaveformCatalog.Blocks.Single(item => item.StartAddress == start);
            var phaseOffset = block.Phase switch
            {
                WaveformPhase.A => 0,
                WaveformPhase.B => 1000,
                WaveformPhase.C => -1000,
                _ => throw new ArgumentOutOfRangeException(),
            };
            return Enumerable.Range(0, count)
                .Select(index => unchecked((ushort)(short)(phaseOffset + block.SegmentIndex * 64 + index)))
                .ToArray();
        });
        var progressEvents = new List<WaveformReadProgress>();
        var trace = new RecordingProtocolTrace();

        var result = await new WaveformDataService(client, trace).ReadAsync(
            4, new InlineProgress<WaveformReadProgress>(progressEvents.Add));

        Assert.Equal(WaveformCatalog.TotalBlocks + 1, client.ReadRequests.Count);
        Assert.Equal(
            (RegisterCatalog.RatedCurrentRegisterAddress, (ushort)1),
            (client.ReadRequests[0].Start, client.ReadRequests[0].Count));
        Assert.Equal(
            WaveformCatalog.Blocks.Select(block => (block.StartAddress, block.Count)),
            client.ReadRequests.Skip(1).Select(request => (request.Start, request.Count)));
        Assert.Equal(384, result.Points.Count);
        Assert.Equal(-80, result.Points[0].TimeMilliseconds);
        Assert.Equal(39.6875, result.Points[^1].TimeMilliseconds);
        Assert.Equal((short)0, result.Points[0].PhaseA);
        Assert.Equal((short)1000, result.Points[0].PhaseB);
        Assert.Equal((short)-1000, result.Points[0].PhaseC);
        Assert.Equal((short)383, result.Points[^1].PhaseA);
        Assert.Equal((short)1383, result.Points[^1].PhaseB);
        Assert.Equal((short)-617, result.Points[^1].PhaseC);
        Assert.Equal((ushort)0xB000, result.Points[0].PhaseAAddress);
        Assert.Equal((ushort)0xB5BF, result.Points[^1].PhaseCAddress);
        Assert.Equal(18, progressEvents.Count);
        Assert.Equal(18, progressEvents[^1].CompletedBlocks);
        Assert.Equal((ushort)0x0204, result.Calibration.RegisterValue);
        Assert.Equal((byte)2, result.Calibration.FrameLevel);
        Assert.Equal(2, result.Calibration.Rate);
        Assert.Equal(WaveformCalibration.RoundAmperes(
            result.PhaseARms * result.Calibration.AmperesPerAd), result.PhaseAAmperesRms);
        Assert.Equal(WaveformCalibration.RoundAmperes(
            result.PhaseBRms * result.Calibration.AmperesPerAd), result.PhaseBAmperesRms);
        Assert.Equal(WaveformCalibration.RoundAmperes(
            result.PhaseCRms * result.Calibration.AmperesPerAd), result.PhaseCAmperesRms);
        Assert.Contains(trace.InformationMessages, message =>
            message.Contains("寄存器1552=516") && message.Contains("框架等级=2") && message.Contains("Rate=2"));
    }

    [Fact]
    public async Task Waveform_read_stops_on_first_failed_block_without_returning_partial_data()
    {
        await using var client = new FakeClient((start, count) =>
        {
            if (start == RegisterCatalog.RatedCurrentRegisterAddress) return [0x0204];
            if (start == 0xB240) throw new TimeoutException("模拟录波超时");
            return new ushort[count];
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WaveformDataService(client).ReadAsync(4));

        Assert.Contains("B 相", exception.Message);
        Assert.Contains("0xB240", exception.Message);
        Assert.Equal(9, client.ReadRequests.Count);
        Assert.Equal((ushort)0xB240, client.ReadRequests[^1].Start);
    }

    [Fact]
    public async Task Waveform_read_stops_before_waveform_blocks_when_calibration_read_fails()
    {
        await using var client = new FakeClient((start, _) =>
            throw new TimeoutException($"寄存器 {start} 超时"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WaveformDataService(client).ReadAsync(4));

        Assert.Contains("1552", exception.Message);
        Assert.Single(client.ReadRequests);
        Assert.Equal((ushort)1552, client.ReadRequests[0].Start);
    }

    [Theory]
    [InlineData(0x0304)]
    [InlineData(0x0F04)]
    public async Task Waveform_read_rejects_unknown_frame_level_before_waveform_blocks(int registerValue)
    {
        await using var client = new FakeClient((_, _) => [(ushort)registerValue]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new WaveformDataService(client).ReadAsync(4));

        Assert.Contains("框架等级", exception.Message);
        Assert.Single(client.ReadRequests);
    }

    private static ushort[] CreateFaultRecord()
    {
        var raw = new ushort[18];
        raw[0] = 0x2607; raw[1] = 0x2214; raw[2] = 0x3009; raw[3] = 0x0700;
        raw[12] = 0x2607; raw[13] = 0x2208; raw[14] = 0x1500; raw[16] = 0x0444;
        raw[17] = 0x0300;
        return raw;
    }

    private sealed class FakeClient(Func<ushort, ushort, ushort[]> read) : IModbusRtuClient
    {
        public bool IsOpen => true;
        public (ushort Address, ushort Value) LastWrite { get; private set; }
        public List<(ushort Start, ushort Count)> ReadRequests { get; } = [];
        public ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort count, CancellationToken cancellationToken = default)
        {
            ReadRequests.Add((startAddress, count));
            return Task.FromResult(read(startAddress, count));
        }
        public Task WriteSingleRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default) { LastWrite = (address, value); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
