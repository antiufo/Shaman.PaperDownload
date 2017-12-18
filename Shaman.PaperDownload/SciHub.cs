using Shaman.Dom;
using Shaman.Runtime;
using Shaman.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if DEVSANDBOX
using HttpUtils = Shaman.Utils;
#endif

namespace Shaman.Connectors.SciHub
{
    public class Paper
    {
        private static Dictionary<string, string> cachedPapers;
        public static async Task DownloadAsync(Uri l, IProgress<DataTransferProgress> progress = null)
        {
            var force = false;
            while (true)
            {
                try
                {
                    await DownloadAttemptAsync(l, force, progress);
                    break;
                }
                catch (CaptchaException ex)
                {
                    progress.Report("Captcha.");
                    var sh = ex.Url.GetLeftPart_UriPartial_Query().AsUri();
                    Console.WriteLine(sh);
                    Console.WriteLine("CAPTCHA: Press ENTER to open browser.");
                    Console.ReadLine();
                    Process.Start(sh.AbsoluteUri);
                    Console.WriteLine("Press ENTER to continue.");
                    Console.ReadLine();
                    force = true;
                }
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = ReseekableStringBuilder.AcquirePooledStringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                sb.Append(Hex[b >> 4]);
                sb.Append(Hex[b & 0xF]);
            }

            return ReseekableStringBuilder.GetValueAndRelease(sb);
        }

        private const string Hex = "0123456789ABCDEF";

        private static async Task<string> DownloadAttemptAsync(Uri url, bool force, IProgress<DataTransferProgress> progress)
        {
            var originalUrl = url;
            string hash;
            using (var sha1 = new System.Security.Cryptography.SHA1Cng())
            {
                hash = ToHex(sha1.ComputeHash(Encoding.UTF8.GetBytes(url.AbsoluteUri))).ToLower().Substring(0, 16);

            }

            lock (typeof(Paper))
            {
                if (cachedPapers == null)
                {

                    cachedPapers = new Dictionary<string, string>();
                    foreach (var x in Directory.EnumerateFiles("/Awdee/SciHub", "*.pdf"))
                    {
                        var name = Path.GetFileName(x);
                        var p = name.IndexOf('-');
                        cachedPapers[p == -1 ? x : name.Substring(0, p)] = x;
                    }
                }
            }

            var r = cachedPapers.TryGetValue(hash);
            if (r != null) return r;
            progress.Report("Initializing");
          //  return;
            WebFile pdfFile = null;
            string title = null;
            HtmlNode original = null;
            var cookies = new IsolatedCookieContainer();


            string doi = null;
            if (url.IsHostedOn("dx.doi.org"))
            {
                doi = HttpUtils.UnescapeDataString(url.AbsolutePath.Substring(1));
            }


            (pdfFile, title) = await TryGetLibgenAsync(doi, null, progress);

            if (pdfFile == null && url.IsHostedOn("academia.edu"))
            {
                cookies.AddRange(HttpUtils.ParseCookies(Configuration_AcademiaEduCookie.Trim()));
                progress.Report("Retrieving from Academia.edu");
                original = await url.GetHtmlNodeAsync(null, cookies);
                var u = new LazyUri( original.GetLinkUrl("a[data-download],.js-swp-download-button"));
                u.AppendCookies(cookies);
                u.AppendFragmentParameter("$header-Referer", url.AbsoluteUri);
                title = original.GetValue(":property('citation_title')");
                pdfFile = WebFile.FromUrlUntracked(u.Url);
            }

            if (pdfFile == null)
            {
                try
                {

                    progress.Report("Retrieving plain page");

                    original = await url.GetHtmlNodeAsync();

                    doi = original.TryGetValue(":property('citation_doi'),meta[scheme='doi']:property('dc.Identifier')");

                    if (doi == null && url.IsHostedOn("nih.gov"))
                    {
                        doi = original.TryGetValue("a[ref='aid_type=doi'],.doi > a");
                        if (doi == null && url.AbsolutePath.StartsWith("/pubmed/"))
                        {
                            progress.Report("Finding DOI on EuropePMC.org");
                            var alt = await HttpUtils.FormatEscaped("http://europepmc.org/abstract/med/{0}", url.GetPathComponent(1)).GetHtmlNodeAsync();
                            doi = alt.TryGetValue("meta[name='citation_doi']", "content");
                        }
                    }

                    if (doi == null && url.IsHostedOn("sciencedirect.com"))
                    {
                        doi = original.TryGetValue("script:json-token('SDM.doi = ')");
                    }

                    if (doi != null)
                    {
                        (pdfFile, title) = await TryGetLibgenAsync(doi, null, progress);
                    }

                    if (pdfFile == null && url.IsHostedOn("researchgate.net"))
                    {
                        var u = FindPdfLink(original);
                        if (u != null) pdfFile = WebFile.FromUrlUntracked(u);
                    }

                    if (title == null)
                    {
                        title = original.TryGetValue(":property('citation_title')");
                        if (title == null)
                        {
                            title = original.TryGetValue("title")?.TrimEnd(" - PubMed - NCBI");
                        }
                    }
                }
                catch (NotSupportedResponseException ex) when (ex.ContentType == "application/pdf")
                {
                    pdfFile = WebFile.FromUrlUntracked(url);
                }
            }
            if (pdfFile == null)
            {
                if (url.IsHostedOn("nlm.nih.gov"))
                {
                    var a = original.TryGetLinkUrl(".portlet a");
                    if (a != null)
                        url = a;
                    else
                    {
                        var k = FindPdfLink(original);
                        if (k != null) pdfFile = WebFile.FromUrlUntracked(k);
                    }
                }


                if (pdfFile == null)
                {

                    if (!url.IsHostedOn("scielo.br"))
                    {
                        var u = new LazyUri("http://" + url.Host + ".sci-hub.cc" + url.AbsolutePath + url.Query + url.Fragment);
                        progress.Report("Trying on SciHub");
                        u.AppendFragmentParameter("$allow-same-redirect", "1");
                        url = u.Url;
                    }
                    else
                    {
                        progress.Report("Trying on " + url.Host);
                    }

                    var scihub = await url.GetHtmlNodeAsync(null, cookies);
                    if (scihub.FindSingle("img#captcha") != null) throw new CaptchaException(scihub.OwnerDocument.PageUrl);

                    if (scihub.OwnerDocument.PageUrl.IsHostedOn("libgen.io"))
                    {
                        var u = scihub.GetLinkUrl("a[href*='/ads.php?']");
                        progress.Report("Found on LibGen.IO");
                        (pdfFile, title) = await TryGetLibgenAsync(null, u, progress);
                    }
                    else
                    {

                        var pdflink = scihub.TryGetLinkUrl("iframe#pdf") ??
                            FindPdfLink(scihub);
                        if (pdflink != null)
                        {
                            var u = new LazyUri(pdflink);
                            u.AppendCookies(cookies);
                            pdfFile = WebFile.FromUrlUntracked(u.Url);
                        }
                    }
                }
            }


            
            if (pdfFile != null)
            {
                var uu = new LazyUri(pdfFile.Url);
                uu.AppendFragmentParameter("$allow-same-redirect", "1");
                uu.AppendFragmentParameter("$forbid-html", "1");
                pdfFile = WebFile.FromUrlUntracked(uu.Url);
                if (title == null)
                {
                    var z = pdfFile.SuggestedFileName;
                    if (z != null)
                        title = Path.GetFileNameWithoutExtension(z);
                }
                else
                {
                    title = title.Trim().TrimEnd(".").RegexReplace(@"\s+", "-");
                }

                progress.Report("Downloading from " + pdfFile.Url.Host);
                string path;
                try
                {
                    path = await pdfFile.DownloadAsync("/Awdee/SciHub", hash + "-" + title + ".pdf", WebFile.FileOverwriteMode.Skip, CancellationToken.None, progress);                    
                }
                catch (NotSupportedResponseException ex)
                {
                    if (ex.Page != null && ex.Page.FindSingle("img#captcha") != null) throw new CaptchaException(ex.Page.OwnerDocument.PageUrl);
                    throw;
                }
                var filename = Path.GetFileName(path);
                lock (typeof(Paper))
                {
                    cachedPapers[hash] = path;
                    File.AppendAllText("/Awdee/SciHubDownloads.csv", string.Join("\t", originalUrl, title, doi, filename, new FileInfo(path).Length) + "\r\n", Encoding.UTF8);
                }
                progress.Report("Done.");
                return path;
            }
            throw new Exception("Could not find any PDF links.");
        }

        private static Uri FindPdfLink(HtmlNode scihub)
        {
            var options = scihub.DescendantsAndSelf("a")
                .Select(x => x.TryGetLinkUrl())
                .Where(x => x != null && HttpUtils.IsHttp(x) && x.AbsolutePath.EndsWith(".pdf"))
                .GroupBy(x => x)
                .ToList();

            if (options.Count <= 1) return options.FirstOrDefault()?.Key;
            else
            {
                return options.OrderByDescending(x => x.Key.AbsoluteUri.Length).First().Key;
            }
        }

        private async static Task<(WebFile, string)> TryGetLibgenAsync(string doi, Uri libgenurl, IProgress<DataTransferProgress> progress)
        {
            if (doi != null || libgenurl != null)
            {
                libgenurl = libgenurl ?? HttpUtils.FormatEscaped("http://libgen.io/scimag/ads.php?doi={0}&downloadname=", doi);
                progress.Report("Trying on LibGen.IO");
                var page = await libgenurl.GetHtmlNodeAsync();
                var citation = page.GetValue("textarea");
                var title = citation.TryCaptureBetween("title = {", "}");
                if (title != null)
                {
                    progress.Report("Found on LibGen.IO");
                    return (WebFile.FromUrlUntracked(page.GetLinkUrl("h2:text-is('GET'):select-parent")), title);
                }
            }
            return (null, null);
        }

        public static bool IsScientificJournalSite(Uri l)
        {
            return Configuration_ScientificJournalSites.Any(x => l.IsHostedOn(x));
        }

        private class CaptchaException : Exception
        {
            public CaptchaException(Uri url)
            {
                this.Url = url;
            }
            public Uri Url;
        }



        [Configuration]
        private static string[] Configuration_ScientificJournalSites = new[] {
            "nih.gov",
            "doi.org",
            //"cdc.gov",
            "apa.org",
            "newscientist.com",
            "sciencedirect.com",
            "sagepub.com",
            "psychology.org.au",
            "jstor.org",
            "edu",
            //"webmd.com",
            "researchgate.net",
            "pnas.org",
            //"psychologytoday.com",
            //"who.int",
            "springer.com",
            "nature.com",
            //"nhs.uk",
            "cpa.ca",
            //"nationalgeographic.com",
            "sciencedaily.com",
            "wiley.com",
            "cbc.ca",
            "bmj.com",
            "scientificamerican.com",
            "elsevierhealth.com",
            "endojournals.org"
        };


        //private static Task DownloadScientificPaperAsync(Uri l)
        //{

        //    var url = new Uri(l.Host + ".sci-hub.cc" + l.AbsolutePath + l.Query + l.Fragment);

        //}

        [Configuration]
        public static string Configuration_AcademiaEduCookie;
    }
}
