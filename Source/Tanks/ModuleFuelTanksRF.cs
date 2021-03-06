﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RealFuels.Tanks
{
    public partial class ModuleFuelTanks : IAnalyticTemperatureModifier, IAnalyticPreview
    {
        protected double totalTankArea;
        private double boiloffMass = 0d;
        private double analyticSkinTemp;
        private double analyticInternalTemp;
        private double previewInternalFluxAdjust;
        private bool supportsBoiloff = false;
        public double sunAndBodyFlux = 0;

        public double outerInsulationFactor = 0.0;

        public int numberOfMLILayers = 0; // base number of layers taken from TANK_DEFINITION configs

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "MLI Layers", guiUnits = "#", guiFormat = "F0"), UI_FloatRange(minValue = 0, maxValue = 100, stepIncrement = 1, scene = UI_Scene.Editor)]
        public float _numberOfAddedMLILayers = 0; // This is the number of layers added by the player.
        public int numberOfAddedMLILayers
        {
           get
            {
                return (int)_numberOfAddedMLILayers;
            }
        }

        // for EngineIgnitor integration: store a public dictionary of all pressurized propellants
        [NonSerialized]
        public Dictionary<string, bool> pressurizedFuels = new Dictionary<string, bool>();

        [KSPField(guiActiveEditor = true, guiName = "Highly Pressurized?")]
        public bool highlyPressurized = false;

        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "Wall Temp.", guiUnits = "")]
        public string debug0Display;

        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "Heat Penetration", guiUnits = "")]
        public string debug1Display;

        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "Boil-off Loss", guiUnits = "")]
        public string debug2Display;

        [KSPField(isPersistant = true)]
        public double partPrevTemperature = -1;

        private static double ConductionFactors => RFSettings.Instance.globalConductionCompensation ? Math.Max(1.0d, PhysicsGlobals.ConductionFactor) : 1d;

        public double BoiloffMassRate => boiloffMass;


        private FlightIntegrator _flightIntegrator;

        double lowestTankTemperature = 300d;

        partial void OnStartRF(StartState state)
        {
            base.OnStart(state);

            GameEvents.onVesselWasModified.Add(OnVesselWasModified);
            GameEvents.onPartDestroyed.Add(OnPartDestroyed);
            if (HighLogic.LoadedSceneIsFlight)
            {
                for (int i = 0; i < vessel.vesselModules.Count; i++)
                {
                    if (vessel.vesselModules[i] is FlightIntegrator)
                    {
                        _flightIntegrator = vessel.vesselModules[i] as FlightIntegrator;
                    }
                }
            }


            CalculateTankArea(out totalTankArea);

            if (outerInsulationFactor > 0.0)
            {
                // TODO Deprecated! Leave in place for legacy purposes but this is moving back to part.skinInternalConductionMult based on calculated Lockheed MLI equations
                // changed from skin-internal to part.heatConductivity which affects only skin-internal
                // part.heatConductivity = Math.Min(part.heatConductivity, outerInsulationFactor);
                // affects how fast internal temperatures change during analytic mode
                // part.analyticInternalInsulationFactor *= outerInsulationFactor;
            }
            for (int i = tankList.Count - 1; i >= 0; --i)
            {
                FuelTank tank = tankList[i];
                if (tank.maxAmount > 0.0 && (tank.vsp > 0.0 || tank.loss_rate > 0.0))
                {
                    supportsBoiloff = true;
                    break;
                }
            }

            Fields[nameof(debug0Display)].guiActive = RFSettings.Instance.debugBoilOff && this.supportsBoiloff;
            Fields[nameof(debug1Display)].guiActive = RFSettings.Instance.debugBoilOff && this.supportsBoiloff;
            Fields[nameof(debug2Display)].guiActive = RFSettings.Instance.debugBoilOff && this.supportsBoiloff;

            //numberOfAddedMLILayers = Mathf.Round(numberOfAddedMLILayers);
            CalculateInsulation();
        }

        private void CalculateInsulation()
        {
            // TODO tie this into insulation configuration GUI! Also, we should handle MLI separately and as part skin-internal conduction. 
            // Dewars and SOFI should be handled separately as part of the boiloff code on a per-tank basis (if Dewar then use dewar heat transfer code instead of conductive)
            // Current SOFI configuration system should be left in place with players able to add to tanks that don't have it.
            if (numberOfMLILayers + (int)numberOfAddedMLILayers > 0)
            {
                double normalizationFactor = 1 / (PhysicsGlobals.SkinInternalConductionFactor * PhysicsGlobals.ConductionFactor * PhysicsGlobals.ThermalConvergenceFactor * part.partInfo.partPrefab.skinInternalConductionMult * 10 * 0.5);
                part.heatConductivity = normalizationFactor * Math.Abs(GetMLITransferRate(part.skinTemperature, part.temperature) / (part.skinTemperature - part.temperature)) * 0.001;
            }
#if DEBUG
            DebugSkinInternalConduction();
#endif

        }

        public void DebugSkinInternalConduction()
        {
            double num12 = part.ptd.conductionMult * 0.5;
            double num13 = part.temperature * part.thermalMass;
            double num14 = part.skinTemperature - part.temperature;
            if (num14 > 0.001 || num14 < -0.001)
            {
                double num15 = part.skinExposedAreaFrac * part.radiativeArea * num12 * PhysicsGlobals.SkinInternalConductionFactor * part.skinInternalConductionMult;
                double num27 = part.skinThermalMass * part.skinExposedAreaFrac;
                double num28 = (num13 + part.skinTemperature * num27) / (part.thermalMass + num27);
                double num23 = -num14 * num15 * part.skinThermalMassRecip * part.skinExposedMassMult;
                double num24 = num14 * num15 * part.thermalMassReciprocal;
                double num25 = part.skinTemperature + num23 - num28;
                double num26 = part.temperature + num24 - num28;
                double val4;
                double val3 = val4 = 1.0;
                if (num14 > 0.0)
                {
                    if (num25< 0.0)
                    {
                        val3 = (num23 - num25) / num23;
                    }
                    if (num26 > 0.0)
                    {
                        val4 = (num24 - num26) / num24;
                    }
                }
                else
                {
                    if (num25 > 0.0)
                    {
                        val3 = (num23 - num25) / num23;
                    }
                    if (num26 < 0.0)
                    {
                        val4 = (num24 - num26) / num24;
                    }
                }
                double num20 = Math.Max(0.0, Math.Min(val4, val3));
                print("part.ptd.skinInteralConductionFlux = " + part.ptd.skinInteralConductionFlux.ToString("F16"));
                print("num15                 = " + num15.ToString("F16"));
                print("num20 (unknownFactor) = " + num20.ToString("F16"));
                print("num14                 = " + num14.ToString("F16"));
            }
            //Debug.Log("part.skinInteralConductionFlux = " + part.ptd.skinInteralConductionFlux.ToString("F16"));
        }

        partial void GetModuleMassRF()
        {
            massDirty = true;
            CalculateMass();
            if (totalTankArea <= 0)
                CalculateTankArea(out totalTankArea);
            
            //numberOfAddedMLILayers = Mathf.Round(numberOfAddedMLILayers);
            massDelta += (float)(0.000015 * totalTankArea * (numberOfMLILayers + numberOfAddedMLILayers));
        }

        partial void GetModuleCostRF()
        {
            if (totalTankArea <= 0)
                CalculateTankArea(out totalTankArea);

            //numberOfAddedMLILayers = Mathf.Round(numberOfAddedMLILayers);
            // TODO Determine cost and add that to cst
            // Estimate material cost at 0.10764/m2 treating as Fund = $1000 (for RO purposes)
            // Plus another 0.1 for installation
            cst += (float)(0.20764 * totalTankArea * (numberOfMLILayers + numberOfAddedMLILayers));
        }

        public void FixedUpdate()
        {
            //print ("[Real Fuels]" + Time.time.ToString ());
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (RFSettings.Instance.debugBoilOff)
                {
                    debug1Display = "";
                    debug2Display = "";
                    debug0Display = "";
                }

                // MLI performance varies by temperature delta
                CalculateInsulation();

                if(!_flightIntegrator.isAnalytical && supportsBoiloff)
                    StartCoroutine(CalculateTankBoiloff(_flightIntegrator.timeSinceLastUpdate));
            }
        }

        private IEnumerator CalculateTankBoiloff(double deltaTime, bool analyticalMode = false)
        {
            // Need to ensure that all heat compensation (radiators, heat pumps, etc) run first.
            if (totalTankArea <= 0)
                CalculateTankArea(out totalTankArea);

            if (!analyticalMode)
                yield return new WaitForFixedUpdate();
            
            boiloffMass = 0d;

            previewInternalFluxAdjust = 0;

            bool hasCryoFuels = CalculateLowestTankTemperature();

            if (tankList.Count > 0 && lowestTankTemperature < 300d && MFSSettings.radiatorMinTempMult >= 0d)
                part.radiatorMax = (lowestTankTemperature * MFSSettings.radiatorMinTempMult) / part.maxTemp;

            if (fueledByLaunchClamp)
            {
                if (hasCryoFuels)
                {
                    part.temperature = lowestTankTemperature;
                    part.skinTemperature = lowestTankTemperature;
                }
                fueledByLaunchClamp = false;
                yield break;
            }

            partPrevTemperature = part.temperature;

            double deltaTimeRecip = 1d / deltaTime;
            //Debug.Log("internalFlux = " + part.thermalInternalFlux.ToString() + ", thermalInternalFluxPrevious =" + part.thermalInternalFluxPrevious.ToString() + ", analytic internal flux = " + previewInternalFluxAdjust.ToString());

            double cooling = analyticalMode ? Math.Min(0, part.thermalInternalFluxPrevious) : 0;

            if (RFSettings.Instance.debugBoilOff)
                debug0Display = part.temperature.ToString("F4") + "(" + GetMLITransferRate(part.skinTemperature, part.temperature).ToString("F4") + " * " + (part.radiativeArea * part.skinExposedAreaFrac).ToString("F2") + "m2)";

            for (int i = tankList.Count - 1; i >= 0; --i)
            {
                FuelTank tank = tankList[i];
                if (tank.amount <= 0) continue;

                if (tank.vsp > 0.0 && tank.totalArea > 0)
                {
                    // Opposite of original boil off code. Finds massLost first.
                    double massLost = 0.0;
                    double deltaTemp;
                    double hotTemp = part.temperature;
                    double tankRatio = tank.maxAmount / volume;

                    if (RFSettings.Instance.ferociousBoilOff)
                        hotTemp = Math.Max(((hotTemp * part.thermalMass) - (tank.temperature * part.resourceThermalMass)) / (part.thermalMass - part.resourceThermalMass), part.temperature);

                    deltaTemp = hotTemp - tank.temperature;

                    if (RFSettings.Instance.debugBoilOff)
                    {
                        if (debug2Display != "")
                            debug2Display += " / ";

                        if (debug1Display != "")
                            debug1Display += " / ";
                    }

                    if (deltaTemp > 0)
                    {
#if DEBUG
                        if (analyticalMode)
                            print("Tank " + tank.name + " surface area = " + tank.totalArea);
#endif

                        double wettedArea = tank.totalArea;// disabled until proper wetted vs ullage conduction can be done (tank.amount / tank.maxAmount);

                        double Q = 0;

                        if (tank.isDewar)
                            Q = GetDewarTransferRate(hotTemp, tank.temperature, tank.totalArea);
                        else
                            Q = deltaTemp /
                                ((tank.wallThickness / (tank.wallConduction * wettedArea))
                                 + (tank.insulationThickness / (tank.insulationConduction * wettedArea))
                                 + (tank.resourceConductivity > 0 ? (0.01 / (tank.resourceConductivity * wettedArea)) : 0));

                        Q *= 0.001d; // convert to kilowatts

                        massLost = Q / tank.vsp;

                        if (RFSettings.Instance.debugBoilOff)
                        {
                            // Only do debugging displays if debugging enabled in RFSettings

                            debug1Display += Utilities.FormatFlux(Q);
                            debug2Display += (massLost * 1000 * 3600).ToString("F4") + "kg/hr";
                        }
                        massLost *= deltaTime; // Frame scaling
                    }

                    double lossAmount = massLost / tank.density;

                    if (double.IsNaN(lossAmount))
                        print("[RF] " + tank.name + " lossAmount is NaN!");
                    else
                    {
                        double heatLost = 0d;
                        if (lossAmount > tank.amount)
                        {
                            tank.amount = 0d;
                        }
                        else
                        {
                            tank.amount -= lossAmount;

                            heatLost = -massLost * tank.vsp;

                            // See if there is boiloff byproduct and see if any other parts want to accept it.
                            if (tank.boiloffProductResource != null)
                            {
                                double boiloffProductAmount = -(massLost / tank.boiloffProductResource.density);
                                double retainedAmount = part.RequestResource(tank.boiloffProductResource.id, boiloffProductAmount, ResourceFlowMode.STAGE_PRIORITY_FLOW);
                                massLost -= retainedAmount * tank.boiloffProductResource.density;
                            }

                            boiloffMass += massLost;

                        }
                        // subtract heat from boiloff
                        // subtracting heat in analytic mode is tricky: Analytic flux handling is 'cheaty' and tricky to predict. 
                        // scratch sheet: example
                        // [RealFuels.ModuleFuelTankRF] proceduralTankRealFuels Analytic Temp = 256.679360297684, Analytic Internal = 256.679360297684, Analytic Skin = 256.679360297684
                        // [RealFuels.ModuleFuelTankRF] proceduralTankRealFuels deltaTime = 17306955.5092776, heat lost = 6638604.21227684, thermalMassReciprocal = 0.00444787360733243

                        if (!analyticalMode)
                        {
                            heatLost *= ConductionFactors;

                                part.AddThermalFlux(heatLost * deltaTimeRecip);
                        }
                        else
                        {
                            analyticInternalTemp = analyticInternalTemp + (heatLost * part.thermalMassReciprocal);
                            previewInternalFluxAdjust += heatLost * deltaTimeRecip;
#if DEBUG
                            if (deltaTime > 0)
                                print(part.name + " deltaTime = " + deltaTime + ", heat lost = " + heatLost + ", thermalMassReciprocal = " + part.thermalMassReciprocal);
#endif
                        }
                    }
                }
                else if (tank.loss_rate > 0 && tank.amount > 0)
                {
                    double deltaTemp = part.temperature - tank.temperature;
                    if (deltaTemp > 0)
                    {
                        double lossAmount = tank.maxAmount * tank.loss_rate * deltaTemp * deltaTime;
                        if (lossAmount > tank.amount)
                        {
                            lossAmount = -tank.amount;
                            tank.amount = 0d;
                        }
                        else
                        {
                            lossAmount = -lossAmount;
                            tank.amount += lossAmount;
                        }
                        double massLost = tank.density * lossAmount;
                        boiloffMass += massLost;
                    }
                }
            }
        }

        partial void UpdateTankTypeRF(TankDefinition def)
        {
            // Get pressurization
            highlyPressurized = def.highlyPressurized;

            if (isDatabaseLoad)
                UpdateEngineIgnitor(def);
        }

        private void UpdateEngineIgnitor(TankDefinition def)
        {
            // collect pressurized propellants for EngineIgnitor
            // XXX Dirty hack until engine ignitor is fixed
            fuelList.Clear();               //XXX
            fuelList.AddRange(tankList);    //XXX

            pressurizedFuels.Clear();
            for (int i = 0; i < tankList.Count; i++)
            {
                FuelTank f = tankList[i];
                pressurizedFuels[f.name] = def.highlyPressurized || f.note.ToLower().Contains("pressurized");
            }
        }

        partial void ParseInsulationFactor(ConfigNode node)
        {
            if (node.HasValue("numberOfMLILayers"))
                int.TryParse(node.GetValue("numberOfMLILayers"), out numberOfMLILayers);
            else if (node.HasValue("outerInsulationFactor"))
                double.TryParse(node.GetValue("outerInsulationFactor"), out outerInsulationFactor);
        }

        public void CalculateTankArea(out double totalTankArea)
        {
            // TODO: Codify a more accurate tank area calculator.
            // Thought: cube YN/YP can be used to find the part diameter / circumference... X or Z finds the length
            // Also should try to determine if tank has a common bulkhead - and adjust heat flux into individual tanks accordingly
            print("CalculateTankArea() running");

            if (HighLogic.LoadedSceneIsEditor)
            {
                if (!this.part.DragCubes.None)
                {
                    this.part.DragCubes.SetDragWeights();
                    this.part.DragCubes.RequestOcclusionUpdate();
                    this.part.DragCubes.SetPartOcclusion();
                }
            }

            totalTankArea = 0f;

            for (int i = 0; i< 6; ++i)
            {
                totalTankArea += part.DragCubes.WeightedArea[i];
            }
#if DEBUG
            Debug.Log("[RF] Part WeightedArea: " + part.name + " = " + totalTankArea.ToString("F2"));
            Debug.Log("[RF] Part Area: " + part.name + " = " + part.DragCubes.Area.ToString("F2"));
#endif
            // This allows a rough guess as to individual tank surface area based on ratio of tank volume to total volume but it breaks down at very small fractions
            // So use greater of spherical calculation and tank ratio of total area.
            // if for any reason our totalTankArea is still 0 (no drag cubes available yet or analytic temp routines executed first)
            // then we're going to be defaulting to spherical calculation
            double tankMaxAmount;
            double tempTotal = 0;
            for (int i = tankList.Count - 1; i >= 0; --i)
            {
                FuelTank tank = tankList[i];
                if (tank.maxAmount > 0.0)
                {
                    tankMaxAmount = tank.maxAmount;

                    if (tank.utilization > 1.0)
                        tankMaxAmount /= tank.utilization;

                    tank.tankRatio = tankMaxAmount / volume;

                    tank.totalArea = Math.Max(Math.Pow(Math.PI, 1.0 / 3.0) * Math.Pow((tankMaxAmount / 1000.0) * 6, 2.0 / 3.0), tank.totalArea = totalTankArea * tank.tankRatio);
                    tempTotal += tank.totalArea;

                    if (RFSettings.Instance.debugBoilOff)
                    {
                        Debug.Log("[RF] " + tank.name + ".tankRatio = " + tank.tankRatio.ToString());
                        Debug.Log("[RF] " + tank.name + ".maxAmount = " + tankMaxAmount.ToString());
                        Debug.Log("[RF] " + part.name + ".totalTankArea = " + totalTankArea.ToString());
                        Debug.Log("[RF] Tank surface area = " + tank.totalArea.ToString());
                    }
                }
            }
            if (!(totalTankArea > 0) || tempTotal > totalTankArea)
                totalTankArea = tempTotal;
        }

        public void OnVesselWasModified(Vessel v)
        {
            Debug.Log("ModuleFuelTanksRF.OnVesselWasModified()");
            if (v != null && v == this.vessel)
                CalculateTankArea(out totalTankArea);
        }

        private void OnPartDestroyed(Part p)
        {
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
            GameEvents.onPartDestroyed.Remove(OnPartDestroyed);
        }

        private bool CalculateLowestTankTemperature()
        {
            bool result = false;
            lowestTankTemperature = 300;
            for (int i = tankList.Count - 1; i >= 0; --i)
            {
                FuelTank tank = tankList[i];
                if (tank.amount > 0d && (tank.vsp > 0.0 || tank.loss_rate > 0d))
                {
                    lowestTankTemperature = Math.Min(lowestTankTemperature, tank.temperature);
                    result = true;
                }
            }
            return result;
        }

        #region IAnalyticTemperatureModifier
        // Analytic Interface
        public void SetAnalyticTemperature(FlightIntegrator fi, double analyticTemp, double toBeInternal, double toBeSkin)
        {
            //if (analyticInternalTemp > lowestTankTemperature)
#if DEBUG
            if(this.supportsBoiloff)
                print(part.name + " Analytic Temp = " + analyticTemp.ToString() + ", Analytic Internal = " + toBeInternal.ToString() + ", Analytic Skin = " + toBeSkin.ToString());
#endif
            
            analyticSkinTemp = toBeSkin;
            analyticInternalTemp = toBeInternal;

            if (this.supportsBoiloff)
            {
                if (fi.timeSinceLastUpdate < double.MaxValue * 0.99)
                {
                    StartCoroutine(CalculateTankBoiloff(fi.timeSinceLastUpdate, true));
                }
                else if (CalculateLowestTankTemperature())
                {
                    // Vessel is freshly spawned and has cryogenic tanks, set temperatures appropriately
                    analyticSkinTemp = lowestTankTemperature;
                    analyticInternalTemp = lowestTankTemperature;
                }
            }
        }

        public double GetSkinTemperature(out bool lerp)
        {
            lerp = true;
            return analyticSkinTemp;
        }

        public double GetInternalTemperature(out bool lerp)
        {
            // During analytic, pin our internal temperature. We'll figure out the difference and apply as much boiloff flux as needed for this to be valid.
            if (supportsBoiloff)
                lerp = true;
            else
                lerp = true;
            return analyticInternalTemp;
        }
        #endregion

        #region Analytic Preview Interface
        public void AnalyticInfo(FlightIntegrator fi, double sunAndBodyIn, double backgroundRadiation, double radArea, double absEmissRatio, double internalFlux, double convCoeff, double ambientTemp, double maxPartTemp)
        {
            if (_flightIntegrator != fi)
                fi = _flightIntegrator;

            sunAndBodyFlux = sunAndBodyIn;
            //previewInternalFluxAdjust = internalFlux;
            //float deltaTime = (float)(Planetarium.GetUniversalTime() - vessel.lastUT);
            //if (this.supportsBoiloff)
            //{
            //    StartCoroutine(CalculateTankBoiloff(TimeWarp.fixedDeltaTime, true));
            //}
        }

        public double InternalFluxAdjust()
        {
            return previewInternalFluxAdjust;
        }
        #endregion

        static void print(string msg)
        {
            MonoBehaviour.print("[RealFuels.ModuleFuelTankRF] " + msg);
        }

        #region Cryogenics

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Insulation Type"), UI_ChooseOption(scene = UI_Scene.Editor)]
        public string insulationType;
        private string oldInsulationType;

        public string[] insulationLevelsAvailable = { "None", "Old", "MLI", "Dewar" };

        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = true, guiName = "Insulation Level", guiUnits = "%", guiFormat = "F0"),
         UI_FloatRange(minValue = 0, maxValue = 100, stepIncrement = 1, scene = UI_Scene.Editor)]
        public float insulationLevel = 0;

        /// <summary>
        /// Transfer rate through multilayer insulation in watts/m2 via radiation and conduction. (convection when in atmo not handled at this time)
        /// Default hot and cold values of 300 / 70. Can be called in real time substituting skin temp and internal temp for hot and cold. 
        /// </summary>
        private double GetMLITransferRate(double outerTemperature = 300, double innerTemperature = 70)
        {            
            // This function assumes vacuum. If we need more accuracy in atmosphere then a convective equation will need to be added (return radiation + conduction + convection). (actual contribution minimal?)
            double QrCoefficient = 0.000000000539; // typical MLI radiation flux coefficient
            double QcCoefficient = 0.0000000895; // typical MLI conductive flux coefficient. Possible tech upgrade target based on spacing mechanism between layers?
            double Emissivity = 0.03; // typical reflective mylar emissivity...?
            float layerDensity = 14.99813f; // 8.51f; // layer density (layers/cm)
            int layers = numberOfMLILayers + (int)numberOfAddedMLILayers;

            double radiation = (QrCoefficient * Emissivity * (Math.Pow(outerTemperature, 4.67) - Math.Pow(innerTemperature, 4.67))) / layers;
            double conduction = ((QcCoefficient * Math.Pow(layerDensity, 2.56) * ((outerTemperature + innerTemperature) / 2)) / (layers + 1)) * (outerTemperature - innerTemperature);
            return radiation + conduction;
        }

        /// <summary>
        /// Transfer rate through Dewar walls
        /// This is simplified down to basic radiation formula using corrected emissivity values for concentric walls for sake of performance
        /// </summary>
        private double GetDewarTransferRate(double hot, double cold, double area)
        {
            // TODO Just radiation now; need to calculate conduction through piping/lid, etc
            double emissivity = 0.005074871897; // corrected and rounded value for concentric surfaces, actual emissivity of each surface is assumed to be 0.01 for silvered or aluminized coating
            return PhysicsGlobals.StefanBoltzmanConstant * emissivity * area * (Math.Pow(hot,4) - Math.Pow(cold,4));
        }

        #endregion
    }
}
