// Copyright (c) 2018 phosphene47
// 
// This software is released under the MIT License.
// https://opensource.org/licenses/MIT

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace nhitomi
{
    public class nhitomiSerializerSettings : JsonSerializerSettings
    {
        public static void Apply(JsonSerializerSettings settings)
        {
            settings.DefaultValueHandling = DefaultValueHandling.Ignore;
            settings.NullValueHandling = NullValueHandling.Ignore;
            settings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        }

        public nhitomiSerializerSettings()
        {
            Apply(this);
        }
    }
}