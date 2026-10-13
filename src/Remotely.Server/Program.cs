using Remotely.Server;

if (args.Length > 0 && !args[0].StartsWith('-') && !args[0].Contains('='))
    return await ServerCommands.RunAsync(args);

await RemotelyServer.Build(args).RunAsync();
return 0;
