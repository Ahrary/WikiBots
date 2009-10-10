﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using Claymore.ArchiveWikiBot.Properties;
using Claymore.SharpMediaWiki;

namespace Claymore.ArchiveWikiBot
{
    internal class Program
    {
        static int Main(string[] args)
        {
            Wiki wiki = new Wiki("http://ru.wikipedia.org");
            wiki.SleepBetweenQueries = 2;
            if (string.IsNullOrEmpty(Settings.Default.Login) ||
                string.IsNullOrEmpty(Settings.Default.Password))
            {
                Console.Out.WriteLine("Please add login and password to the configuration file.");
                return 0;
            }

            Console.Out.WriteLine("Logging in as " + Settings.Default.Login + "...");
            try
            {
                if (!wiki.LoadCookies())
                {
                    wiki.Login(Settings.Default.Login, Settings.Default.Password);
                    wiki.CacheCookies();
                }
                else
                {
                    wiki.Login();
                    if (!wiki.IsBot)
                    {
                        wiki.Logout();
                        wiki.Login(Settings.Default.Login, Settings.Default.Password);
                        wiki.CacheCookies();
                    }
                }
            }
            catch (WikiException e)
            {
                Console.Out.WriteLine(e.Message);
                return 0;
            }
            Console.Out.WriteLine("Logged in as " + Settings.Default.Login + ".");

            string listText = wiki.LoadPage("Участник:ClaymoreBot/Архивация/Список");
            StringReader reader = new StringReader(listText);
            HashSet<string> pages = new HashSet<string>();
            Regex pageRE = new Regex(@"^\*\s*(.+)\s*$");
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                Match m = pageRE.Match(line);
                if (m.Success)
                {
                    pages.Add(m.Groups[1].Value);
                }
            }

            ParameterCollection parameters = new ParameterCollection();
            parameters.Add("generator", "embeddedin");
            parameters.Add("geititle", "Участник:ClaymoreBot/Архивация");
            parameters.Add("geilimit", "max");
            parameters.Add("prop", "info");
            parameters.Add("intoken", "edit");
            parameters.Add("redirects");

            XmlDocument doc = wiki.Enumerate(parameters, true);
            foreach (XmlNode node in doc.SelectNodes("//page"))
            {
                string title = node.Attributes["title"].Value;
                string path = @"Cache\ru\" + Cache.EscapePath(title) + @"\";
                Directory.CreateDirectory(path);
                WikiPage page = Cache.Load(wiki, title, path);
                Archive archive;
                if (TryParse(page, path, pages.Contains(page.Title), out archive))
                {
                    try
                    {
                        archive.Archivate(wiki);
                    }
                    catch (WikiException)
                    {
                    }
                }
            }
            return 0;
        }

        private static bool TryParseTemplate(string text, out Dictionary<string, string> parameters)
        {
            parameters = null;
            Regex templateRE = new Regex(@"\{\{(Участник|User):ClaymoreBot/Архиваци(я).",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            Match m = templateRE.Match(text);
            if (!m.Success)
            {
                return false;
            }
            int index = 1;
            int begin = m.Groups[2].Index + 1;
            int end = -1;
            for (int i = begin; i < text.Length - 1; ++i)
            {
                if (text[i] == '{' && text[i + 1] == '{')
                {
                    ++index;
                }
                else if (text[i] == '}' && text[i + 1] == '}')
                {
                    --index;
                    if (index == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end == -1)
            {
                return false;
            }

            parameters = new Dictionary<string, string>();
            string parameterString = text.Substring(begin, end - begin);
            string[] ps = parameterString.Split(new char[] { '|' });
            string lastKey = "";
            foreach (var p in ps)
            {
                string[] keyvalue = p.Split(new char[] { '=' });
                if (keyvalue.Length == 2)
                {
                    parameters.Add(keyvalue[0].Trim().ToLower(), keyvalue[1].Trim());
                    lastKey = keyvalue[0].Trim().ToLower();
                }
                else if (keyvalue.Length == 1)
                {
                    if (!string.IsNullOrEmpty(lastKey))
                    {
                        parameters[lastKey] = parameters[lastKey] + "|" + keyvalue[0].Trim();
                    }
                }
            }
            return true;
        }

        public static bool TryParse(WikiPage page,
                                    string directory,
                                    bool allowSource,
                                    out Archive archive)
        {
            archive = null;
            Dictionary<string, string> values;
            if (!TryParseTemplate(page.Text, out values))
            {
                return false;
            }
            
            int days = 14;
            if (values.ContainsKey("срок"))
            {
                int.TryParse(values["срок"], out days);
            }
            
            int archiveSize = 70 * 1024;
            if (values.ContainsKey("размер архива"))
            {
                int.TryParse(values["размер архива"], out archiveSize);
            }

            bool checkForResult = false;
            if (values.ContainsKey("итог"))
            {
                string value = values["итог"].ToLower();
                if (value == "да")
                {
                    checkForResult = true;
                }
                else
                {
                    checkForResult = true;
                }
            }

            string pageName = page.Title;
            if (allowSource && values.ContainsKey("обрабатывать"))
            {
                pageName = values["обрабатывать"];
            }

            string format = pageName + "/Архив/%(номер)";
            if (values.ContainsKey("формат"))
            {
                format = pageName + "/" + values["формат"];
            }

            string header = "{{closed}}\n";
            if (values.ContainsKey("заголовок"))
            {
                header = values["заголовок"];
            }

            if (values.ContainsKey("страница"))
            {
                format = pageName + "/" + values["страница"];
            }

            if (allowSource && values.ContainsKey("абсолютный путь"))
            {
                format = values["формат"];
            }

            int topics = 0;
            if (values.ContainsKey("тем в архиве"))
            {
                int.TryParse(values["тем в архиве"], out topics);
            }
            bool newSectionsDown = true;
            if (values.ContainsKey("новые"))
            {
                if (values["новые"].ToLower() == "сверху")
                {
                    newSectionsDown = false;
                }
            }
            if (values.ContainsKey("тип"))
            {
                string t = values["тип"].ToLower();
                if (t == "страница")
                {
                    archive = new Archive(pageName, directory, days, format, header, checkForResult, newSectionsDown);
                }
                else if (t == "месяц")
                {
                    archive = new ArchiveByMonth(pageName, directory, days, format, header, checkForResult, newSectionsDown);
                }
                else if (t == "год")
                {
                    archive = new ArchiveByYear(pageName, directory, days, format, header, checkForResult, newSectionsDown);
                }
                else if (t == "полгода")
                {
                    archive = new ArchiveByHalfYear(pageName, directory, days, format, header, checkForResult, newSectionsDown);
                }
                else if (t == "статьи для рецензирования")
                {
                    archive = new ReviewArchive(pageName, directory, days, format, header);
                }
                else if (t == "нумерация" && topics > 0)
                {
                    archive = new ArchiveByTopicNumber(pageName, directory, days, format, header, checkForResult, newSectionsDown, topics);
                }
            }
            if (archive != null)
            {
                if (values.ContainsKey("убирать ссылки") &&
                        values["убирать ссылки"].ToLower() == "да")
                {
                    archive.Processor = RemoveHttp;
                }
                return true;
            }
            return false;
        }

        private static string RemoveHttp(string title)
        {
            return title.Replace("http://", "");
        }
    }
}