using HtmlAgilityPack;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinkTester
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var filePath = args[0];
            var fileText = File.ReadAllText(filePath);

            var lines = GetLines(fileText);
            var links = GetLinksFromCsv(lines);

            var uriBuilders = links.Select(link => new UriBuilder(link));

            var resultText = new StringBuilder();
            var responses = SendRequests(uriBuilders).ToObservable();

            var notFound = responses.Where(response => response.StatusCode == 0 || response.StatusCode == HttpStatusCode.NotFound);
            notFound.Subscribe(response =>
            {
                var uri = response.Request.Resource;
                WriteLineColor(uri, ConsoleColor.Yellow);
                resultText.AppendLine($"{uri},,{(int)response.StatusCode}");
            });

            var blocked = responses.Where(response =>
                response.Headers.FirstOrDefault(h => h.Name == "Server")?.Value as string == "Cisco Umbrella");
            blocked.Subscribe(response =>
            {
                var uri = response.Request.Resource;
                WriteLineColor(uri, ConsoleColor.Red);
                resultText.AppendLine($"{uri},,BLOCKED");
            });

            var success = responses.Where(response =>
                    !(response.StatusCode == 0 || response.StatusCode == HttpStatusCode.NotFound))
                .Where(response =>
                    !(response.Headers.FirstOrDefault(h => h.Name == "Server")?.Value as string == "Cisco Umbrella"));
            success.Subscribe(response =>
            {
                var uri = response.Request.Resource;
                WriteLineColor(uri, ConsoleColor.Green);
                resultText.AppendLine($"{uri},,");
            });

            var deepSearch = success.SelectMany(response =>
            {
                var parentUri = response.Request.Resource;

                var links = GetLinksFromHtml(response.Content);

                return links.Select(link =>
                {
                    if (link.StartsWith("http") && Uri.IsWellFormedUriString(link, UriKind.Absolute))
                    {
                        return (parentUri: parentUri, uriBuilder: new UriBuilder(new Uri(link)));
                    }

                    // Build relative uri
                    if (!Uri.IsWellFormedUriString(link, UriKind.Relative))
                        return (parentUri: parentUri, uriBuilder: null);

                    return (parentUri: parentUri, uriBuilder: new UriBuilder(new Uri(new Uri(parentUri), new Uri(link, UriKind.Relative))));
                });
            }).Where(x => x.uriBuilder != null).SelectMany( x =>
            {
                var (parentUri, uriBuilder) = x;
                return SendRequest(uriBuilder).ToObservable()
                    .Where(response => response != null)
                    .Select(response => (parentUri: parentUri, response: response));
            });

            var deepSearchNotFound = deepSearch.Where(x => x.response.StatusCode == 0 || x.response.StatusCode == HttpStatusCode.NotFound);
            deepSearchNotFound.Subscribe(x =>
            {
                var uri = x.response.Request.Resource;
                WriteLineColor($"> {uri}", ConsoleColor.Yellow);
                resultText.AppendLine($"{x.parentUri},{uri},{(int)x.response.StatusCode}");
            });

            var deepSearchBlocked = deepSearch.Where(x =>
                x.response.Headers.FirstOrDefault(h => h.Name == "Server")?.Value as string == "Cisco Umbrella");
            deepSearchBlocked.Subscribe(x =>
            {
                var uri = x.response.Request.Resource;
                WriteLineColor($"> {uri}", ConsoleColor.Red);
                resultText.AppendLine($"{x.parentUri},{uri},BLOCKED");
            });

            var deepSearchSuccess = deepSearch.Where(x =>
                    !(x.response.StatusCode == 0 || x.response.StatusCode == HttpStatusCode.NotFound))
                .Where(x =>
                    !(x.response.Headers.FirstOrDefault(h => h.Name == "Server")?.Value as string == "Cisco Umbrella"));
            deepSearchSuccess.Subscribe(x =>
            {
                var uri = x.response.Request.Resource;
                WriteLineColor($"> {uri}", ConsoleColor.Green);
                resultText.AppendLine($"{x.parentUri},{uri},");
            });

            await responses;

            File.WriteAllText(filePath, resultText.ToString());
        }

        private static async IAsyncEnumerable<IRestResponse> SendRequest(UriBuilder uriBuilder)
        {
            var request = new RestRequest(uriBuilder.Uri, Method.GET);
            IRestResponse response = null;
            try
            {
                var client = new RestClient();
                response = await client.ExecuteAsync(request);
            }
            catch{}

            yield return response;
        }

        private static async IAsyncEnumerable<IRestResponse> SendRequests(IEnumerable<UriBuilder> uriBuilders)
        {
            foreach (var uriBuilder in uriBuilders)
            {
                var request = new RestRequest(uriBuilder.Uri, Method.GET);
                var client = new RestClient();
                var response = await client.ExecuteAsync(request);
                if (response.StatusCode != 0) { yield return response; continue; }
                uriBuilder.Scheme = "http";
                uriBuilder.Port = -1;
                request = new RestRequest(uriBuilder.Uri, Method.GET);
                yield return await client.ExecuteAsync(request);
            }
        }

        private static List<string> GetLinksFromHtml(string content)
        {
            var links = new List<string>();

            try
            {
                var document = new HtmlDocument();
                document.LoadHtml(content);

                var root = document.DocumentNode;
                var aNodes = root.Descendants("a");

                foreach (var node in aNodes)
                {
                    var link = node.GetAttributeValue("href", "");
                    links.Add(link);
                }
            }
            catch {}
            
            return links;
        }

        private static void WriteLineColor(string value, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ResetColor();
        }

        private static string[] GetLinksFromCsv(string[] lines)
        {
            return lines.Select(x => x.Split(',').FirstOrDefault())
                .Select(x =>
                {
                    if (x != null &&
                        !x.StartsWith("http", StringComparison.InvariantCultureIgnoreCase) &&
                        !x.StartsWith("https", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return $"https://{x}";
                    }
                    return x;
                })
                .Select(x => x?.Replace("*", ""))
                .Select(x => x?.Trim()).ToArray();
        }

        private static string[] GetLines(string fileText)
        {
            return fileText.Split(new []{ "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
