﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.UI;
using DotNetNuke;
using DotNetNuke.Common;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Users;
using DotNetNuke.Services.Url.FriendlyUrl;
using FortyFingers.SeoRedirect.Components.Data;

namespace FortyFingers.SeoRedirect.Components
{
    public static class RedirectController
    {

        public static UserInfo UserInfo
        {
            get
            {
                return UserController.GetCurrentUserInfo();
            }
        }

        private static HttpResponse Response
        {
            get { return HttpContext.Current.Response; }
        }

        private static HttpRequest Request
        {
            get { return HttpContext.Current.Request; }
        }

        public static void DoRedirect(ControlCollection logToControls, bool redirectWhenNo404Detected = false, bool onlyLogWhen404 = false)
        {
            // find incoming URL
            string incoming = "";

            try
            {
                // nothing to do if it's already a redirect because of an error
                //if (!String.IsNullOrEmpty(Request.QueryString["aspxerrorpath"]))
                //    return;

                bool is404 = false;
                // we're matching lowercased: case insensitive
                incoming = IncomingUrl(logToControls, ref is404).ToLower();
                // enable logging when DNN detects a 404 later on (e.g. for extentionless urls)
                HttpContext.Current.Items["40F_SEO_IncomingUrl"] = incoming;
                // register whether or not a 404 was detected
                RedirectController.SetRequest404Detected(is404);

                // since generating a 404 inside a 404 page 
                // (which is what we do in case of .aspx errors)
                // will cause an additional redirect to the error page,
                // we here check if the incoming url contains an aspx error path
                // if so: stop processing, we're already done.
                if (HasAspxError(incoming))
                {
                    SetStatus404();
                    return;
                }

                if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("Incoming: {0}<br/>", incoming)));

                // check if URL is in Sources of mappingtable
                string target = "";

                var mappingsNoRegex = RedirectConfig.Instance.MappingsDictionary(false);
                
                if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("Mappings (with regex): {0}<br/>", RedirectConfig.Instance.MappingsDictionary(true).Count)));
                if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("Mappings (no regex): {0}<br/>", mappingsNoRegex.Count)));


                bool addRedirectLogging = true;
                // if we're in a 404, let's try to find a mapping
                if (is404 || redirectWhenNo404Detected)
                {
                    Common.Logger.Debug($"Try to find a mapping for [{incoming}]");
                    // first try non-regex mappings, as they're supposed to be faster
                    if (mappingsNoRegex.ContainsKey(incoming))
                    {
                        target = mappingsNoRegex[incoming];
                        HttpContext.Current.Items["40F_SEO_MappingFound"] = true;
                        addRedirectLogging = RedirectConfig.Instance.IsLoggingEnabled(incoming);
                        Common.Logger.Debug($"Mapping without regex found, Target: [{target}]");
                    }
                    else if (mappingsNoRegex.ContainsKey(ToRelativeUrl(incoming)))
                    {
                        target = mappingsNoRegex[ToRelativeUrl(incoming)];
                        HttpContext.Current.Items["40F_SEO_MappingFound"] = true;
                        addRedirectLogging = RedirectConfig.Instance.IsLoggingEnabled(incoming);
                        Common.Logger.Debug($"Mapping without regex found, Target: [{target}]");
                    }
                    else
                    {
                        // no match found without regexes, try the ones with regex
                        var mappingsUsingRegex = RedirectConfig.Instance.MappingsDictionary(true);

                        // now try and match each one
                        foreach (var mapping in mappingsUsingRegex)
                        {
                            var mappingSource = ToFullUrl(mapping.Key);
                            var mappingTarget = ToFullUrl(mapping.Value);

                            if (Regex.IsMatch(incoming, mappingSource))
                            {
                                // got a match!
                                target = Regex.Replace(incoming, mappingSource, mappingTarget);
                                HttpContext.Current.Items["40F_SEO_MappingFound"] = true;
                                addRedirectLogging = RedirectConfig.Instance.IsLoggingEnabled(mapping.Key);
                                Common.Logger.Debug($"Mapping with regex found, Target: [{target}]");
                            }
                        }
                    }
                }

                // if there should not be logged, register the HttpItem for that
                if(!addRedirectLogging) HttpContext.Current.Items["40F_SEO_AlreadyLogged"] = true;

                // Log this 404
                var ps = Common.CurrentPortalSettings;
                if (addRedirectLogging && (is404 || (redirectWhenNo404Detected && !string.IsNullOrEmpty(target))))
                {
                    Common.Logger.Debug($"Logging redirect: is404:{is404}, redirectWhenNo404Detected:{redirectWhenNo404Detected}, target:[{target}]");
                    AddRedirectLog(ps.PortalId, incoming, target);
                }
                else if (addRedirectLogging && !onlyLogWhen404)
                {
                    Common.Logger.Debug($"Logging redirect for !onlyLogWhen404 target:[{target}]");
                    AddRedirectLog(ps.PortalId, incoming, target);
                }

                if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("Target: {0}<br/>", target)));
                // if so: redirect
                if (!String.IsNullOrEmpty(target))
                {
                    try
                    {
                        Common.Logger.Debug($"Redirect to:[{target}]");
                        Response.Redirect(target, false);
                        Response.StatusCode = 301;
                        Response.End();
                    }
                    catch (Exception)
                    {
                        // do nothing: threadabortexception is normal behaviour
                    }
                }

                // we're only displaying the logging if it's a 404
                if (!is404)
                {
                    logToControls.Clear();
                }

                //else if (is404) // only if it was a 404 incoming
                //{
                //    // tell the client that the page wasn't found
                //    SetStatus404();
                //}
            }
            catch (Exception exception)
            {
                Common.Logger.Error($"Exception in DoRedirect: {exception.Message}. Stacktrace: {exception.StackTrace}");
                // we're not writing in the eventlog, since the amount of log records can really explode
                if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("Error: {0}<br/>", exception.Message + "<br/>" + exception.StackTrace)));
            }

        }

        private static string ToFullUrl(string relativeUrl)
        {
            var retval = relativeUrl;
            if (retval.StartsWith("/"))
            {
                retval = Globals.AddHTTP(Common.CurrentPortalSettings.PortalAlias.HTTPAlias + retval);
            }
            return retval;
        }
        private static string ToRelativeUrl(string fullUrl)
        {
            var retval = fullUrl;

            retval = retval.Replace("http://", "").Replace("https://", "");

            retval = retval.Substring(retval.IndexOf("/"));


            return retval;
        }

        internal static void SetStatus404()
        {
            Common.Logger.Debug($"Setting ResponseStatus to 404");
            Response.Status = "404 Not Found";
            Response.StatusCode = 404;
        }

        internal static void SetRequest404Detected(bool is404)
        {
            if (HttpContext.Current.Items["40F_SEO_404Detected"] != null) return;
            HttpContext.Current.Items["40F_SEO_404Detected"] = is404;
        }
        internal static bool RequestHas404Detected()
        {
            if (HttpContext.Current.Items["40F_SEO_404Detected"] == null) return false;
            return (bool)HttpContext.Current.Items["40F_SEO_404Detected"];
        }

        internal static void AddRedirectLog(int portalId, string incoming, string target)
        {
            if (HttpContext.Current.Items["40F_SEO_AlreadyLogged"] != null) return;
            
            DataProvider.Instance()
                        .AddRedirectLog(portalId, incoming, DateTime.UtcNow,
                                        Request.UrlReferrer == null ? "" : Request.UrlReferrer.ToString(),
                                        Request.ServerVariables.AllKeys.Contains("HTTP_USER_AGENT") ? Request.ServerVariables["HTTP_USER_AGENT"] : "",
                                        target, HttpContext.Current.Items["40F_SEO_MappingFound"] != null);
            // clear the context item so it isn't logged twice
            HttpContext.Current.Items["40F_SEO_IncomingUrl"] = "";
            HttpContext.Current.Items["40F_SEO_AlreadyLogged"] = true;

        }
        //private static void AddRedirectLog(IList<string> values)
        //{
        //    DataProvider.Instance()
        //                .AddRedirectLog(Common.CurrentPortalSettings.PortalId, values[0], DateTime.UtcNow, Request.UrlReferrer.ToString(),
        //                                Request.ServerVariables["HTTP_USER_AGENT"], values[1]);
        //    // clear the context item so it isn't logged twice
        //    HttpContext.Current.Items["40F_SEO_IncomingUrl"] = "";
        //    HttpContext.Current.Items["40F_SEO_AlreadyLogged"] = true;
        //}
        //internal static void AddRedirectLogAsync(string incoming, string target)
        //{
        //    var values = new List<string>()
        //        {incoming, target};

        //    Action<IList<string>> action = AddRedirectLog;
        //    action.BeginInvoke(values, null, null);
        //}

        public static List<RedirectLogUrl> GetTopUnhandledUrls(int portalId, int maxDays, int maxUrls)
        {
            var startdate = DateTime.Today.AddDays(-1 * maxDays);
            var dr = DataProvider.Instance().GetTopUnhandledUrls(portalId, startdate, maxUrls);

            var retval = CBO.FillCollection<RedirectLogUrl>(dr);

            return retval;
        }

        public static void SetHandledUrl(string url)
        {
            DataProvider.Instance().SetHandledUrl(url, DateTime.Now, UserController.GetCurrentUserInfo().Username);
        }

        public static string IncomingUrl(ControlCollection logToControls, ref bool is404)
        {
            string incoming = "";

            //Request.RawUrl	"/404.aspx?404;http://dev_seo.local:80/banaan.asp?id=123123"	string
            //Request.RawUrl	"/404.aspx?404;http://dev_seo.local:80/banaan.asp"	string
            //Request.RawUrl	"/404.aspx?404;http://dev_seo.local:80/banaan"	string
            //Request.RawUrl	"/404.aspx?404;http://dev_seo.local:80/banaan.jsf?asdasd"	string
            //Request.RawUrl	"/404.aspx?aspxerrorpath=/asdasd/dfgdfg/werwer/bla.aspx"	string
            //Request.RawUrl	"/404.aspx?aspxerrorpath=/banaan.aspx"	string

            if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("RawUrl: {0}<br/>", Request.RawUrl)));
            if (UserInfo.IsSuperUser) logToControls.Add(new LiteralControl(String.Format("AbsoluteUri: {0}<br/>", Request.Url.AbsoluteUri)));

            Regex MyRegex = new Regex("^.*(?i:404;)(.*)",
                                      RegexOptions.CultureInvariant | RegexOptions.Compiled);
            if (MyRegex.IsMatch(Request.RawUrl))
            {
                Match m = MyRegex.Match(Request.RawUrl);
                incoming = m.Groups[1].Captures[0].Value;

                // remove port
                incoming = Regex.Replace(incoming, "(:\\d+)", "");
                Common.Logger.Debug($"Incoming found with \"404;\" method: {incoming}");
            }

            // if incoming is not found try the AbsoluteUri
            if (String.IsNullOrEmpty(incoming))
            {
                var absoluteUri = HttpUtility.UrlDecode(Request.Url.AbsoluteUri);
                if (MyRegex.IsMatch(absoluteUri))
                {
                    //Match m = MyRegex.Match(absoluteUri);
                    //incoming = m.Groups[1].Captures[0].Value;

                    // remove port
                    // incoming = Regex.Replace(incoming, "(:\\d+)", "");

                    incoming = String.Format("{0}://{1}{2}",
                        Request.Url.Scheme,
                        Request.Url.Host,
                        Request.RawUrl);

                    Common.Logger.Debug($"Incoming found with AbsoluteUri method: {incoming}");
                }
            }

            // if incoming is not found try the other option
            if (String.IsNullOrEmpty(incoming))
            {
                if (HasAspxError(Request.RawUrl))
                {
                    Match m = AspxErrorRegex.Match(Request.RawUrl);
                    incoming = m.Groups[1].Captures[0].Value;
                    Common.Logger.Debug($"Incoming found with HasAspxError method: {incoming}");
                }

                // in this case, the path is reletative to the website root, so we need to
                // put hostname in front of it
                if (!String.IsNullOrEmpty(incoming))
                {
                    incoming = String.Format("{0}://{1}{2}{3}",
                        Request.Url.Scheme,
                        Request.Url.Host,
                        incoming.StartsWith("/") ? "" : "/",
                        incoming);

                    Common.Logger.Debug($"Incoming changed to: {incoming}");
                }
            }

            // if still no incoming found, then we're not here by 404
            // let's just get the RawUrl in this case
            // 2015-05-12: this used to take the AbsoluteUri
            if (String.IsNullOrEmpty(incoming))
            {
                is404 = false;
                incoming = String.Format("{0}://{1}{2}",
                    Request.Url.Scheme,
                    Request.Url.Host,
                    Request.RawUrl);
                Common.Logger.Debug($"Incoming for not-404 set to: {incoming}");
            }
            else
            {
                is404 = true;
            }
            return incoming;

        }

        private static Regex AspxErrorRegex = new Regex("^.*(?i:aspxerrorpath=)(.*)",
                    RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static bool HasAspxError(string url)
        {
            return AspxErrorRegex.IsMatch(url);
        }
    }
}