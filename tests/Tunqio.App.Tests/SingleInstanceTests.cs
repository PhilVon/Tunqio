using Tunqio.App.Activation;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S1, AC-475: the instance key is the data root, so a scratch profile is its own instance. Also the two pieces of
/// redirection that are plain logic: splitting a redirected launch's one-string command line, and holding activations
/// that arrive before the window does.
/// </summary>
public sealed class SingleInstanceTests
{
    // ---- the key ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Spellings_of_one_data_root_are_one_instance()
    {
        string key = InstanceKey.ForDataRoot(@"C:\Scratch\Profile");

        InstanceKey.ForDataRoot(@"c:\scratch\PROFILE").Should().Be(key, "NTFS paths are case-insensitive");
        InstanceKey.ForDataRoot(@"C:\Scratch\Profile\").Should().Be(key, "a trailing separator names the same folder");
        InstanceKey.ForDataRoot(@"C:\Scratch\Other\..\Profile").Should().Be(key, "and so does a path through ..");
    }

    [Fact]
    public void Different_data_roots_are_different_instances()
    {
        InstanceKey.ForDataRoot(@"C:\Scratch\One").Should().NotBe(InstanceKey.ForDataRoot(@"C:\Scratch\Two"));
    }

    [Fact]
    public void The_default_profile_is_its_own_instance_and_a_scratch_root_never_shares_its_key()
    {
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tunqio");

        InstanceKey.For(null).Should().Be(InstanceKey.ForDataRoot(real), "no --data-root is the default profile");
        InstanceKey.For(Path.Combine(Path.GetTempPath(), "tunqio-scratch")).Should().NotBe(InstanceKey.For(null),
            "a harness on a scratch profile must never redirect into the app somebody is using");
    }

    [Fact]
    public void A_key_names_the_product_and_has_a_fixed_length_whatever_the_root()
    {
        string shortKey = InstanceKey.ForDataRoot(@"C:\a");
        string longKey = InstanceKey.ForDataRoot(@"C:\" + new string('x', 200));

        shortKey.Should().StartWith(InstanceKey.Prefix);
        longKey.Length.Should().Be(shortKey.Length);
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--data-root", @"C:\scratch" }, true)]
    [InlineData(new[] { @"D:\Music\song.flac" }, true)]
    [InlineData(new[] { "--library-spike" }, false)]
    [InlineData(new[] { "--render-spike" }, false)]
    [InlineData(new[] { "--nowplaying-spike" }, false)]
    [InlineData(new[] { "--shell-spike" }, false)]
    public void Measurement_modes_stay_out_of_single_instance(string[] args, bool applies)
    {
        InstanceKey.Applies(args).Should().Be(applies);
    }

    // ---- the redirected command line --------------------------------------------------------------------------------------

    [Fact]
    public void A_command_line_splits_the_way_the_runtime_split_argv()
    {
        CommandLineText.Split("\"C:\\Program Files\\Tunqio\\Tunqio.exe\" --data-root \"C:\\scratch root\" D:\\a.flac")
            .Should().Equal([@"C:\Program Files\Tunqio\Tunqio.exe", "--data-root", @"C:\scratch root", @"D:\a.flac"]);
    }

    [Theory]
    [InlineData("a\\\\\"b c\"", "a\\b c")]
    [InlineData("a\\\"b", "a\"b")]
    [InlineData("\"say \"\"hi\"\"\"", "say \"hi\"")]
    [InlineData("C:\\a\\b.flac", "C:\\a\\b.flac")]
    [InlineData("\"\"", "")]
    public void Quotes_and_backslashes_follow_the_runtime_s_rules(string commandLine, string expected)
    {
        CommandLineText.Split(commandLine).Should().Equal([expected]);
    }

    [Fact]
    public void Blank_input_is_no_arguments_and_tabs_separate()
    {
        CommandLineText.Split(null).Should().BeEmpty();
        CommandLineText.Split("   ").Should().BeEmpty();
        CommandLineText.Split("a\tb").Should().Equal(["a", "b"]);
    }

    [Fact]
    public void An_unpackaged_launch_s_program_name_is_dropped_and_a_packaged_launch_s_arguments_are_kept()
    {
        CommandLineText.WithoutExecutable([@"D:\app\Tunqio.exe", "tunqio://toggle"]).Should().Equal(["tunqio://toggle"]);
        CommandLineText.WithoutExecutable(["tunqio://toggle"]).Should().Equal(["tunqio://toggle"]);
        CommandLineText.WithoutExecutable([]).Should().BeEmpty();
    }

    // ---- the inbox --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Activations_that_arrive_before_the_window_are_delivered_in_order_when_it_attaches()
    {
        var inbox = new ActivationInbox();
        var delivered = new List<string>();
        inbox.Post(["first"]);
        inbox.Post(["second"]);
        inbox.PendingCount.Should().Be(2);

        inbox.Attach(tokens => delivered.Add(tokens[0]));
        inbox.Post(["third"]);

        delivered.Should().Equal(["first", "second", "third"]);
        inbox.PendingCount.Should().Be(0);
    }
}
