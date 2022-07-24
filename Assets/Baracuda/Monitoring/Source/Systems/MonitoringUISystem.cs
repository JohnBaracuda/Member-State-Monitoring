﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Baracuda.Monitoring.API;
using Baracuda.Monitoring.Source.Interfaces;
using Baracuda.Monitoring.Source.Utilities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Baracuda.Monitoring.Source.Systems
{
    internal class MonitoringUISystem : IMonitoringUI
    {
        private MonitoringUIController _controllerInstance;
        private bool _bufferUICreation = false;

        /*
         * Visibility API   
         */
        
        public void Show()
        {
            if (_controllerInstance)
            {
                _controllerInstance.ShowMonitoringUI();
                VisibleStateChanged?.Invoke(true);
            }
        }

        public void Hide()
        {
            if (_controllerInstance)
            {
                _controllerInstance.HideMonitoringUI();
                VisibleStateChanged?.Invoke(false);
            }
        }

        public bool ToggleDisplay()
        {
            if (_controllerInstance == null)
            {
                return false;
            }

            if (_controllerInstance.IsVisible())
            {
                _controllerInstance.HideMonitoringUI();
                VisibleStateChanged?.Invoke(false);
            }
            else
            {
                _controllerInstance.ShowMonitoringUI();
                VisibleStateChanged?.Invoke(true);
            }

            return IsVisible();
        }

        public event Action<bool> VisibleStateChanged;

        public bool IsVisible()
        {
            return _controllerInstance != null && _controllerInstance.IsVisible();
        }

        public MonitoringUIController GetActiveUIController()
        {
            return _controllerInstance;
        }

        public TUIController GetActiveUIController<TUIController>() where TUIController : MonitoringUIController
        {
            return _controllerInstance as TUIController;
        }

        /*
         * Ctor
         */

        internal MonitoringUISystem()
        {
            if (MonitoringSystems.Resolve<IMonitoringSettings>().EnableMonitoring)
            {
                MonitoringSystems.Resolve<IMonitoringManager>().ProfilingCompleted  += (staticUnits, instanceUnits) =>
                {
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                    {
                        return;
                    }
#endif
                    var settings = MonitoringSystems.Resolve<IMonitoringSettings>();

                    if (settings.AutoInstantiateUI || _bufferUICreation)
                    {
                        var manager = MonitoringSystems.Resolve<IMonitoringManager>();
                        InstantiateMonitoringUI(manager, settings, staticUnits, instanceUnits);
                    }
                };
            }
        }

        /*
         * Instantiation   
         */

        public void CreateMonitoringUI()
        {
            _bufferUICreation = true;
            var manager = MonitoringSystems.Resolve<IMonitoringManager>();

            if (!manager.IsInitialized)
            {
                return;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                return;
            }
#endif
            
            // We return if there is an active UIController.
            if (GetActiveUIController())
            {
                Debug.Log("UIController already instantiated!");
                return;
            }

            var settings = MonitoringSystems.Resolve<IMonitoringSettings>();
            var instanceUnits = manager.GetInstanceUnits();
            var staticUnits = manager.GetStaticUnits();
            InstantiateMonitoringUI(manager, settings, instanceUnits, staticUnits);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InstantiateMonitoringUI(IMonitoringManager manager, IMonitoringSettings settings, IReadOnlyList<IMonitorUnit> staticUnits,
            IReadOnlyList<IMonitorUnit> instanceUnits)
        {
            if (settings.UIControllerUIController == null)
            {
                return;
            }
            
            _controllerInstance = Object.Instantiate(settings.UIControllerUIController);
            
            Object.DontDestroyOnLoad(_controllerInstance.gameObject);
            _controllerInstance.gameObject.hideFlags = settings.ShowRuntimeUIController ? HideFlags.None : HideFlags.HideInHierarchy;

            manager.UnitCreated += _controllerInstance.OnUnitCreated;
            manager.UnitDisposed += _controllerInstance.OnUnitDisposed;
            
            Application.quitting += () =>
            {
                manager.UnitCreated -= _controllerInstance.OnUnitCreated;
                manager.UnitDisposed -= _controllerInstance.OnUnitDisposed;
            };
            
            for (var i = 0; i < staticUnits.Count; i++)
            {
                _controllerInstance.OnUnitCreated(staticUnits[i]);
            }

            for (var i = 0; i < instanceUnits.Count; i++)
            {
                _controllerInstance.OnUnitCreated(instanceUnits[i]);
            }

            if (settings.OpenDisplayOnLoad)
            {
                Show();
            }
            else
            {
                Hide();
            }
        }

        /*
         * Filtering   
         */
        
        public void Filter(string filter)
        {
            MonitoringSystems.Resolve<IMonitoringTicker>().ValidationTickEnabled = false;
            var list = MonitoringSystems.Resolve<IMonitoringManager>().GetAllMonitoringUnits();
            for (var i = 0; i < list.Count; i++)
            {
                var unit = list[i];
                var tags = unit.Profile.Tags;
                var unitEnabled = false;
                if (unit.Name.NoSpace().IndexOf(filter.NoSpace(), StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    unit.Enabled = true;
                    continue;
                }
                for (var j = 0; j < tags.Length; j++)
                {
                    if (tags[j].NoSpace().IndexOf(filter.NoSpace(), StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        unitEnabled = true;
                        break;
                    }
                }
                unit.Enabled = unitEnabled;
            }
        }

        public void ResetFilter()
        {
            MonitoringSystems.Resolve<IMonitoringTicker>().ValidationTickEnabled = true;
            foreach (var unit in MonitoringSystems.Resolve<IMonitoringManager>().GetAllMonitoringUnits())
            {
                unit.Enabled = true;
            }
        }
    }
}