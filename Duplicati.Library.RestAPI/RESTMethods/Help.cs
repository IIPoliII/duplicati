// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Duplicati.Server.WebServer.RESTMethods
{
    public class Help : IRESTMethodGET
    {
        public void GET(string key, RequestInfo info)
        {
            var sb = new StringBuilder();
            if (string.IsNullOrWhiteSpace(key))
            {
                foreach(var m in RESTHandler.Modules.Keys.OrderBy(x => x))
                {                    
                    var mod = RESTHandler.Modules[m];
                    if (mod == this)
                        continue;

                    var desc = mod.GetType().Name;
                    if (mod is IRESTMethodDocumented documented)
                        desc = documented.Description;
                    sb.AppendFormat(ITEM_TEMPLATE, RESTHandler.API_URI_PATH, m, mod.GetType().Name, desc);
                }


                var data = Encoding.UTF8.GetBytes(string.Format(TEMPLATE, "API Information", "", sb));

                info.Response.ContentType = "text/html";
                info.Response.ContentLength = data.Length;
                info.Response.Body.Write(data, 0, data.Length);
                info.Response.Send();
            }
            else
            {
                IRESTMethod m;
                RESTHandler.Modules.TryGetValue(key, out m);
                if (m == null)
                {
                    info.Response.Status = System.Net.HttpStatusCode.NotFound;
                    info.Response.Reason = "Module not found";
                }
                else
                {
                    var desc = "";
                    if (m is IRESTMethodDocumented doc)
                    {
                        desc = doc.Description;
                        foreach(var t in doc.Types)
                            sb.AppendFormat(METHOD_TEMPLATE, t.Key, JsonConvert.SerializeObject(t.Value)); //TODO: Format the type
                    }

                    var data = Encoding.UTF8.GetBytes(string.Format(TEMPLATE, m.GetType().Name, desc, sb));

                    info.Response.ContentType = "text/html";
                    info.Response.ContentLength = data.Length;
                    info.Response.Body.Write(data, 0, data.Length);
                    info.Response.Send();
                   
                }
            }
        }

        private const string TEMPLATE = @"
<html><head><title>{0}</title></head>
<body>
<h1>{0}</h1>
<p>{1}</p>
<ul>
{2}
</ul>
</body>
";
        private const string ITEM_TEMPLATE = @"
<li><a href=""{0}/help/{1}"">{2}</a>: {3}</li>
";

        private const string METHOD_TEMPLATE = @"
<b>{0}:</b><br><code>{1}</code><br>
";
    }
}

