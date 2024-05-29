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

using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Duplicati.Library.Utility
{
    [SupportedOSPlatform("windows")]
    public static class RegistryUtility
    {
        public static string GetDataByValueName(string parentKeyName, string name)
        {
            try
            {
                // check currentUser first, if null, then check LocalMachine
                // if a registry item does not exist a null is returned
                var cuParentKey = Registry.CurrentUser.OpenSubKey(parentKeyName, RegistryKeyPermissionCheck.ReadSubTree);
                if (cuParentKey?.GetValue(name) != null)
                {
                    return cuParentKey.GetValue(name).ToString();
                }

                var lmParentKey = Registry.LocalMachine.OpenSubKey(parentKeyName, RegistryKeyPermissionCheck.ReadSubTree);
                return lmParentKey?.GetValue(name).ToString();
            }
            catch
            {
                // NOOP
            }

            return null;
        }

    }
}
