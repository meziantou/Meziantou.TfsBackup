using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.Common;

namespace Meziantou.TfsBackup
{
    class Program
    {
        static void Main(string[] args)
        {
            var app = new CommandLineApplication();
            app.Command("download", command =>
            {
                command.HelpOption("--help");
                var tfsArg = command.Argument("tfs", "", multipleValues: false);
                var pathArg = command.Argument("path", "", multipleValues: false);

                var scopeOption = command.Option("--scopePath", "", CommandOptionType.SingleValue);
                var skipPackagesOption = command.Option("--skipPackages", "", CommandOptionType.NoValue);

                command.OnExecute(() =>
                {
                    var baseUrl = tfsArg.Value;
                    string rootFolder = pathArg.Value;

                    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(rootFolder))
                    {
                        command.ShowHelp();
                        return 1;
                    }

                    Func<TfvcItem, bool> filter = item => true;
                    if (skipPackagesOption.HasValue())
                    {
                        filter = item => !item.Path.Contains("/packages/");
                    }
                    
                    DownloadAsync(baseUrl, rootFolder, scopeOption.Value() ?? "$/", filter).Wait();
                    return 0;
                });
            });

            app.HelpOption("--help");
            app.Execute(args);
        }

        private static async Task DownloadAsync(string tfsUrl, string destinationFolder, string scope ,Func<TfvcItem, bool> filter)
        {
            var baseUrl = new Uri(tfsUrl);
            VssClientCredentials vssClientCredentials = new VssClientCredentials();
            vssClientCredentials.Storage = new VssClientCredentialStorage();

            var vssHttpRequestSettings = new VssHttpRequestSettings();
            vssHttpRequestSettings.SendTimeout = TimeSpan.FromMilliseconds(-1);
            var client = new TfvcHttpClient(baseUrl, vssClientCredentials, vssHttpRequestSettings);

            try
            {
                var items = await client.GetItemsAsync(scopePath: scope, recursionLevel: VersionControlRecursionType.Full, includeLinks: false).ConfigureAwait(false);
                var files = items.Where(filter).OrderBy(_ => _.Path).ToList();

                var transformBlock = new TransformBlock<TfvcItem, TfvcItem>(async item =>
                {
                    if (item.IsFolder)
                    {
                        var fullPath = GetFullPath(destinationFolder, item.Path);
                        if (!Directory.Exists(fullPath))
                        {
                            Directory.CreateDirectory(fullPath);
                        }
                    }
                    else
                    {
                        var fullPath = GetFullPath(destinationFolder, item.Path);
                        var folderPath = Path.GetDirectoryName(fullPath);
                        if (folderPath != null && !Directory.Exists(folderPath))
                        {
                            Directory.CreateDirectory(folderPath);
                        }

                        using (var stream = await client.GetItemContentAsync(item.Path))
                        using (var fs = File.Create(fullPath))
                        {
                            await stream.CopyToAsync(fs).ConfigureAwait(false);
                        }

                    }

                    return item;
                }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 8 });

                var writePathBlock = new ActionBlock<TfvcItem>(c =>
                {
                    var index = files.IndexOf(c);
                    Console.WriteLine($"{index}/{files.Count}: {c.Path}");
                });
                transformBlock.LinkTo(writePathBlock, new DataflowLinkOptions { PropagateCompletion = true });

                foreach (var item in files)
                {
                    transformBlock.Post(item);
                }

                transformBlock.Complete();
                await transformBlock.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static string GetFullPath(string rootFolder, string path)
        {
            if (path.StartsWith("$/"))
            {
                path = path.Substring(2);
            }

            return Path.Combine(rootFolder, path);
        }
    }
}
