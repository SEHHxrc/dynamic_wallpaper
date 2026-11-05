using System.ComponentModel;
using FluentAssertions;
using LiveWall.Platform.Windows.Lifecycle;

namespace LiveWall.Windows.IntegrationTests;

public sealed class WindowsProcessShutdownOrderTests
{
    [Theory]
    [InlineData(WindowsShutdownRole.Host, 0x281U)]
    [InlineData(WindowsShutdownRole.Renderer, 0x280U)]
    public void RoleOrderIsApplicationScopedReadBackAndRestoredExactlyOnce(WindowsShutdownRole role, uint expected)
    {
        FakeParameters native = new();
        WindowsProcessShutdownOrder lease = new(role, native);
        lease.Level.Should().Be(expected);
        native.Level.Should().Be(expected);
        native.Flags.Should().Be(0, "do not set SHUTDOWN_NORETRY");
        expected.Should().BeInRange(0x200U, 0x2FFU);
        WindowsProcessShutdownOrder.HostLevel.Should().BeGreaterThan(WindowsProcessShutdownOrder.RendererLevel);
        lease.Dispose();
        lease.Dispose();
        native.Level.Should().Be(0x250U);
        native.Flags.Should().Be(1U);
        native.Sets.Should().HaveCount(2);
    }

    [Fact]
    public void InitialReadFailureDoesNotSetAnyOrder()
    {
        FakeParameters native = new() { FailGetCall = 1 };
        Action create = () => _ = new WindowsProcessShutdownOrder(WindowsShutdownRole.Host, native);
        create.Should().Throw<Win32Exception>();
        native.Sets.Should().BeEmpty();
    }

    [Fact]
    public void SetFailureIsNotSilentlyIgnored()
    {
        FakeParameters native = new() { FailSetCall = 1 };
        Action create = () => _ = new WindowsProcessShutdownOrder(WindowsShutdownRole.Host, native);
        create.Should().Throw<Win32Exception>();
        native.Level.Should().Be(0x250U);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadbackFailureOrMismatchRestoresPreviousParameters(bool mismatch)
    {
        FakeParameters native = new() { FailGetCall = mismatch ? 0 : 2, IgnoreFirstSet = mismatch };
        Action create = () => _ = new WindowsProcessShutdownOrder(WindowsShutdownRole.Host, native);
        create.Should().Throw<InvalidOperationException>();
        native.Sets.Should().HaveCount(2);
        native.Level.Should().Be(0x250U);
        native.Flags.Should().Be(1U);
    }

    [Fact]
    public void RestoreFailureIsVisibleAndDoesNotRepeatNativeWrites()
    {
        FakeParameters native = new() { FailSetCall = 2 };
        WindowsProcessShutdownOrder lease = new(WindowsShutdownRole.Host, native);
        Action dispose = lease.Dispose;
        dispose.Should().Throw<Win32Exception>();
        lease.Dispose();
        native.Sets.Should().HaveCount(2);
    }

    [Fact]
    public void InvalidRoleIsRejectedBeforeNativeAccess()
    {
        FakeParameters native = new();
        Action create = () => _ = new WindowsProcessShutdownOrder((WindowsShutdownRole)99, native);
        create.Should().Throw<ArgumentOutOfRangeException>();
        native.GetCalls.Should().Be(0);
        native.Sets.Should().BeEmpty();
    }

    private sealed class FakeParameters : IProcessShutdownParameters
    {
        public uint Level { get; private set; } = 0x250;
        public uint Flags { get; private set; } = 1;
        public int GetCalls { get; private set; }
        public int FailGetCall { get; init; }
        public int FailSetCall { get; init; }
        public bool IgnoreFirstSet { get; init; }
        public List<(uint Level, uint Flags)> Sets { get; } = [];

        public bool Get(out uint level, out uint flags)
        {
            GetCalls++;
            level = Level;
            flags = Flags;
            return GetCalls != FailGetCall;
        }

        public bool Set(uint level, uint flags)
        {
            Sets.Add((level, flags));
            if (Sets.Count == FailSetCall) return false;
            if (!IgnoreFirstSet || Sets.Count != 1) { Level = level; Flags = flags; }
            return true;
        }
    }
}
