using System;
using System.IO;
using System.Linq;
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

                command.OnExecute(() =>
                {
                    var baseUrl = tfsArg.Value;
                    string rootFolder = pathArg.Value;

                    if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(rootFolder))
                    {
                        command.ShowHelp();
                        return 1;
                    }

                    DownloadAsync(baseUrl, rootFolder).Wait();
                    return 0;
                });
            });

            app.HelpOption("--help");
            app.Execute(args);
        }

        private static async Task DownloadAsync(string tfsUrl, string destinationFolder)
        {
            var baseUrl = new Uri(tfsUrl);
            VssClientCredentials vssClientCredentials = new VssClientCredentials();
            vssClientCredentials.Storage = new VssClientCredentialStorage();

            var vssHttpRequestSettings = new VssHttpRequestSettings();
            vssHttpRequestSettings.SendTimeout = TimeSpan.FromMilliseconds(-1);
            var client = new TfvcHttpClient(baseUrl, vssClientCredentials, vssHttpRequestSettings);

            try
            {
                var items = await client.GetItemsAsync(scopePath: "$/", recursionLevel: VersionControlRecursionType.Full, includeLinks: true).ConfigureAwait(false);

                //var files = items.Select(item => item.Path).OrderBy(_ => _).ToList();

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
                    var index = items.IndexOf(c);
                    Console.WriteLine($"{index}/{items.Count}: {c.Path}");
                });
                transformBlock.LinkTo(writePathBlock, new DataflowLinkOptions { PropagateCompletion = true });

                foreach (var item in items)
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
