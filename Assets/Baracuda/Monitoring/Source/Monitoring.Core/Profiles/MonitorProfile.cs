// Copyright (c) 2022 Jonathan Lang

using Baracuda.Monitoring.Attributes;
using Baracuda.Monitoring.Core.Interfaces;
using Baracuda.Monitoring.Core.Types;
using Baracuda.Monitoring.Core.Units;
using Baracuda.Monitoring.Core.Utilities.Extensions;
using Baracuda.Monitoring.Core.Utilities.Pooling;
using Baracuda.Monitoring.Interfaces;
using Baracuda.Monitoring.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Baracuda.Monitoring.Core.Types.FormatData;

namespace Baracuda.Monitoring.Core.Profiles
{
    internal abstract class MonitorProfile : IMonitorProfile
    {
        #region --- Interface ---

        public MonitorAttribute Attribute { get; }
        public MemberInfo MemberInfo { get; }
        public MemberType MemberType { get; }
        public bool ReceiveTick { get; protected set; } = true;
        public bool DefaultEnabled { get; } = true;
        public Type DeclaringType { get; }
        public Type MonitoredMemberType { get; }
        public bool IsStatic { get; }
        public string[] Tags { get; }
        public string[] CustomTags { get; } = Array.Empty<string>();
        public IFormatData FormatData { get; }

        public bool TryGetMetaAttribute<TAttribute>(out TAttribute attribute) where TAttribute : MonitoringMetaAttribute
        {
            attribute = GetMetaAttribute<TAttribute>();
            return attribute != null;
        }

        public TAttribute GetMetaAttribute<TAttribute>() where TAttribute : MonitoringMetaAttribute
        {
            _metaAttributes.TryGetValue(typeof(TAttribute), out var metaAttribute);
            return metaAttribute as TAttribute;
        }

        #endregion

        #region --- Fields ---

        private readonly Dictionary<Type, MonitoringMetaAttribute> _metaAttributes =
            new Dictionary<Type, MonitoringMetaAttribute>();

        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Ctor ---

        protected MonitorProfile(
            MemberInfo memberInfo,
            MonitorAttribute attribute,
            Type declaringType,
            Type unitValueType,
            MemberType unityType,
            MonitorProfileCtorArgs args)
        {
            Attribute = attribute;
            MemberInfo = memberInfo;
            DeclaringType = args.DeclaringStruct ?? declaringType;
            MonitoredMemberType = unitValueType;
            MemberType = unityType;

            var intFlag = (int) args.ReflectedMemberFlags;
            IsStatic = intFlag.HasFlagFast((int) BindingFlags.Static);

            var settings = args.Settings;
            var memberAttributeCollectionType = memberInfo.GetCustomAttribute<MAttributeCollection>(true)?.GetType();
            var classAttributeCollectionType = declaringType.GetCustomAttribute<MAttributeCollection>(true)?.GetType();

            // Member
            foreach (var metaAttribute in memberInfo.GetCustomAttributes<MonitoringMetaAttribute>(true))
            {
                var key = metaAttribute is MOptionsAttribute ? typeof(MOptionsAttribute) : metaAttribute.GetType();
                if (!_metaAttributes.ContainsKey(key))
                {
                    _metaAttributes.Add(key, metaAttribute);
                }
            }
            // Member attribute collection
            foreach (var metaAttribute in memberAttributeCollectionType?.GetCustomAttributes<MonitoringMetaAttribute>(true) ?? Enumerable.Empty<MonitoringMetaAttribute>())
            {
                var key = metaAttribute is MOptionsAttribute ? typeof(MOptionsAttribute) : metaAttribute.GetType();
                if (!_metaAttributes.ContainsKey(key))
                {
                    _metaAttributes.Add(key, metaAttribute);
                }
            }
            // Class scoped.
            foreach (var metaAttribute in declaringType.GetCustomAttributes<MonitoringMetaAttribute>(true))
            {
                var key = metaAttribute is MOptionsAttribute ? typeof(MOptionsAttribute) : metaAttribute.GetType();
                if (!_metaAttributes.ContainsKey(key))
                {
                    _metaAttributes.Add(key, metaAttribute);
                }
            }
            // Class attribute collection.
            foreach (var metaAttribute in classAttributeCollectionType?.GetCustomAttributes<MonitoringMetaAttribute>(true) ?? Enumerable.Empty<MonitoringMetaAttribute>())
            {
                var key = metaAttribute is MOptionsAttribute ? typeof(MOptionsAttribute) : metaAttribute.GetType();
                if (!_metaAttributes.ContainsKey(key))
                {
                    _metaAttributes.Add(key, metaAttribute);
                }
            }

            var utility = MonitoringSystems.Resolve<IMonitoringUtilityInternal>();

            if (TryGetMetaAttribute<MFontNameAttribute>(out var fontAttribute))
            {
                utility.AddFontHash(fontAttribute.FontHash);
            }

            if (TryGetMetaAttribute<MVisibleAttribute>(out var enabledAttribute))
            {
                DefaultEnabled = enabledAttribute.Visible;
            }
            else if (TryGetMetaAttribute<MOptionsAttribute>(out var optionsAttribute))
            {
                DefaultEnabled = optionsAttribute.Enabled;
            }

            FormatData = CreateFormatData(this, settings);

            var tags = ListPool<string>.Get();

            if (settings.FilterLabel)
            {
                tags.Add(FormatData.Label);
            }

            if (settings.FilterMemberType)
            {
                tags.Add(MemberType.ToString());
            }

            if (settings.FilterStaticOrInstance)
            {
                tags.Add(IsStatic ? "Static" : "Instance");
            }

            if (settings.FilterInterfaces && declaringType.IsInterface)
            {
                tags.Add("Interface");
            }

            if (settings.FilterDeclaringType)
            {
                tags.Add(DeclaringType.Name);
            }

            if (settings.FilterType)
            {
                var readableString = MonitoredMemberType.HumanizedName();
                tags.Add(readableString);
                utility.AddTypeString(readableString);
            }

            if (settings.FilterTags)
            {
                var customTags = ListPool<string>.Get();
                if (TryGetMetaAttribute<MOptionsAttribute>(out var optionsAttribute))
                {
                    foreach (var tag in optionsAttribute.Tags)
                    {
                        customTags.Add(tag);
                        utility.AddTag(tag);
                        tags.Add(tag);
                    }
                }
                if (memberInfo.TryGetCustomAttribute<MTagAttribute>(out var memberTags))
                {
                    foreach (var tag in memberTags.Tags)
                    {
                        customTags.Add(tag);
                        utility.AddTag(tag);
                        tags.Add(tag);
                    }
                }
                if (declaringType.TryGetCustomAttribute<MTagAttribute>(out var classTags))
                {
                    foreach (var tag in classTags.Tags)
                    {
                        customTags.Add(tag);
                        utility.AddTag(tag);
                        tags.Add(tag);
                    }
                }
                CustomTags = customTags.ToArray();
                ListPool<string>.Release(customTags);
            }
            Tags = tags.ToArray();
            ListPool<string>.Release(tags);
        }

        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Factory ---

        /// <summary>
        /// Creates a <see cref="MonitorUnit"/> with the <see cref="MonitorProfile"/>.
        /// </summary>
        /// <param name="target">The target of the unit. Null if static</param>
        /// <returns></returns>
        internal abstract MonitorUnit CreateUnit(object target);

        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Reflection Fields ---

        protected const BindingFlags StaticFlags
            = BindingFlags.Default |
              BindingFlags.Static |
              BindingFlags.Public |
              BindingFlags.NonPublic |
              BindingFlags.DeclaredOnly;

        protected const BindingFlags InstanceFlags
            = BindingFlags.Default |
              BindingFlags.Public |
              BindingFlags.NonPublic |
              BindingFlags.DeclaredOnly |
              BindingFlags.Instance;

        #endregion

        //--------------------------------------------------------------------------------------------------------------

        #region --- Validator ---



        #endregion
    }
}