using Ampelum.Samples.ChatFrontend;
using Ampelum.Samples.Shared;

var fake = args.Contains("--fake");

if (fake)
{
    var script = new FakeScript();
    using var scripted = script.CreateBackend();
    return await ChatDemo.RunAsync(scripted, Console.Out, script.FailPrimaryNextCall) ? 0 : 1;
}

var config = SampleConfig.Load();
using var live = ChatBackend.Live(config.Require, config.CreateChatClient);
return await ChatDemo.RunAsync(live, Console.Out, failPrimary: null) ? 0 : 1;
