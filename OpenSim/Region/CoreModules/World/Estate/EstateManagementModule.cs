/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Timers;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using RegionFlags = OpenMetaverse.RegionFlags;

namespace OpenSim.Region.CoreModules.World.Estate
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EstateManagementModule")]
    public class EstateManagementModule : IEstateModule, INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Timer m_regionChangeTimer = new Timer();
        public Scene Scene { get; private set; }
        public IUserManagement UserManager { get; private set; }
        
        protected EstateManagementCommands m_commands;                

        /// <summary>
        /// If false, region restart requests from the client are blocked even if they are otherwise legitimate.
        /// </summary>
        public bool AllowRegionRestartFromClient { get; set; }

        private EstateTerrainXferHandler TerrainUploader;
        public TelehubManager m_Telehub;

        public event ChangeDelegate OnRegionInfoChange;
        public event ChangeDelegate OnEstateInfoChange;
        public event MessageDelegate OnEstateMessage;

        private int m_delayCount = 0;

        #region Region Module interface
        
        public string Name { get { return "EstateManagementModule"; } }
        
        public Type ReplaceableInterface { get { return null; } }        

        public void Initialise(IConfigSource source) 
        {
            AllowRegionRestartFromClient = true;

            IConfig config = source.Configs["EstateManagement"];

            if (config != null)
                AllowRegionRestartFromClient = config.GetBoolean("AllowRegionRestartFromClient", true);
        }
        
        public void AddRegion(Scene scene)
        {
            Scene = scene;
            Scene.RegisterModuleInterface<IEstateModule>(this);
            Scene.EventManager.OnNewClient += EventManager_OnNewClient;
            Scene.EventManager.OnRequestChangeWaterHeight += changeWaterHeight;

            m_Telehub = new TelehubManager(scene);

            m_commands = new EstateManagementCommands(this);
            m_commands.Initialise();
        }
        
        public void RemoveRegion(Scene scene) {}            
        
        public void RegionLoaded(Scene scene)
        {
            // Sets up the sun module based no the saved Estate and Region Settings
            // DO NOT REMOVE or the sun will stop working
            scene.TriggerEstateSunUpdate();
            
            UserManager = scene.RequestModuleInterface<IUserManagement>();            
        }

        public void Close() 
        {
            m_commands.Close();
        }

        #endregion

        #region Packet Data Responders

        private void clientSendDetailedEstateData(IClientAPI remote_client, UUID invoice)
        {
            sendDetailedEstateData(remote_client, invoice);
            sendEstateLists(remote_client, invoice);
        }

        private void sendDetailedEstateData(IClientAPI remote_client, UUID invoice)
        {
            uint sun = 0;

            if (Scene.RegionInfo.EstateSettings.FixedSun)
                sun = (uint)(Scene.RegionInfo.EstateSettings.SunPosition * 1024.0) + 0x1800;
            UUID estateOwner;
            estateOwner = Scene.RegionInfo.EstateSettings.EstateOwner;

            if (Scene.Permissions.IsGod(remote_client.AgentId))
                estateOwner = remote_client.AgentId;

            remote_client.SendDetailedEstateData(invoice,
                    Scene.RegionInfo.EstateSettings.EstateName,
                    Scene.RegionInfo.EstateSettings.EstateID,
                    Scene.RegionInfo.EstateSettings.ParentEstateID,
                    GetEstateFlags(),
                    sun,
                    Scene.RegionInfo.RegionSettings.Covenant,
                    (uint) Scene.RegionInfo.RegionSettings.CovenantChangedDateTime,
                    Scene.RegionInfo.EstateSettings.AbuseEmail,
                    estateOwner);
        }

        private void sendEstateLists(IClientAPI remote_client, UUID invoice)
        {
            remote_client.SendEstateList(invoice,
                    (int)Constants.EstateAccessCodex.EstateManagers,
                    Scene.RegionInfo.EstateSettings.EstateManagers,
                    Scene.RegionInfo.EstateSettings.EstateID);

            remote_client.SendEstateList(invoice,
                    (int)Constants.EstateAccessCodex.AccessOptions,
                    Scene.RegionInfo.EstateSettings.EstateAccess,
                    Scene.RegionInfo.EstateSettings.EstateID);

            remote_client.SendEstateList(invoice,
                    (int)Constants.EstateAccessCodex.AllowedGroups,
                    Scene.RegionInfo.EstateSettings.EstateGroups,
                    Scene.RegionInfo.EstateSettings.EstateID);

            remote_client.SendBannedUserList(invoice,
                    Scene.RegionInfo.EstateSettings.EstateBans,
                    Scene.RegionInfo.EstateSettings.EstateID);
        }

        private void estateSetRegionInfoHandler(bool blockTerraform, bool noFly, bool allowDamage, bool blockLandResell, int maxAgents, float objectBonusFactor,
                                                int matureLevel, bool restrictPushObject, bool allowParcelChanges)
        {
            if (blockTerraform)
                Scene.RegionInfo.RegionSettings.BlockTerraform = true;
            else
                Scene.RegionInfo.RegionSettings.BlockTerraform = false;

            if (noFly)
                Scene.RegionInfo.RegionSettings.BlockFly = true;
            else
                Scene.RegionInfo.RegionSettings.BlockFly = false;

            if (allowDamage)
                Scene.RegionInfo.RegionSettings.AllowDamage = true;
            else
                Scene.RegionInfo.RegionSettings.AllowDamage = false;

            if (blockLandResell)
                Scene.RegionInfo.RegionSettings.AllowLandResell = false;
            else
                Scene.RegionInfo.RegionSettings.AllowLandResell = true;

            if((byte)maxAgents <= Scene.RegionInfo.AgentCapacity)
                Scene.RegionInfo.RegionSettings.AgentLimit = (byte) maxAgents;
			else
                Scene.RegionInfo.RegionSettings.AgentLimit = Scene.RegionInfo.AgentCapacity;

            Scene.RegionInfo.RegionSettings.ObjectBonus = objectBonusFactor;

            if (matureLevel <= 13)
                Scene.RegionInfo.RegionSettings.Maturity = 0;
            else if (matureLevel <= 21)
                Scene.RegionInfo.RegionSettings.Maturity = 1;
            else
                Scene.RegionInfo.RegionSettings.Maturity = 2;

            if (restrictPushObject)
                Scene.RegionInfo.RegionSettings.RestrictPushing = true;
            else
                Scene.RegionInfo.RegionSettings.RestrictPushing = false;

            if (allowParcelChanges)
                Scene.RegionInfo.RegionSettings.AllowLandJoinDivide = true;
            else
                Scene.RegionInfo.RegionSettings.AllowLandJoinDivide = false;

            Scene.RegionInfo.RegionSettings.Save();
            TriggerRegionInfoChange();

            sendRegionInfoPacketToAll();
        }

        public void setEstateTerrainBaseTexture(int level, UUID texture)
        {
            setEstateTerrainBaseTexture(null, level, texture);
            sendRegionHandshakeToAll();
        }

        public void setEstateTerrainBaseTexture(IClientAPI remoteClient, int level, UUID texture)
        {
            if (texture == UUID.Zero)
                return;

            switch (level)
            {
                case 0:
                    Scene.RegionInfo.RegionSettings.TerrainTexture1 = texture;
                    break;
                case 1:
                    Scene.RegionInfo.RegionSettings.TerrainTexture2 = texture;
                    break;
                case 2:
                    Scene.RegionInfo.RegionSettings.TerrainTexture3 = texture;
                    break;
                case 3:
                    Scene.RegionInfo.RegionSettings.TerrainTexture4 = texture;
                    break;
            }

            Scene.RegionInfo.RegionSettings.Save();
            TriggerRegionInfoChange();
            sendRegionInfoPacketToAll();
        }

        public void setEstateTerrainTextureHeights(int corner, float lowValue, float highValue)
        {
            setEstateTerrainTextureHeights(null, corner, lowValue, highValue);
        }

        public void setEstateTerrainTextureHeights(IClientAPI client, int corner, float lowValue, float highValue)
        {
            switch (corner)
            {
                case 0:
                    Scene.RegionInfo.RegionSettings.Elevation1SW = lowValue;
                    Scene.RegionInfo.RegionSettings.Elevation2SW = highValue;
                    break;
                case 1:
                    Scene.RegionInfo.RegionSettings.Elevation1NW = lowValue;
                    Scene.RegionInfo.RegionSettings.Elevation2NW = highValue;
                    break;
                case 2:
                    Scene.RegionInfo.RegionSettings.Elevation1SE = lowValue;
                    Scene.RegionInfo.RegionSettings.Elevation2SE = highValue;
                    break;
                case 3:
                    Scene.RegionInfo.RegionSettings.Elevation1NE = lowValue;
                    Scene.RegionInfo.RegionSettings.Elevation2NE = highValue;
                    break;
            }

            Scene.RegionInfo.RegionSettings.Save();
            TriggerRegionInfoChange();
            sendRegionHandshakeToAll();
            sendRegionInfoPacketToAll();
        }

        private void handleCommitEstateTerrainTextureRequest(IClientAPI remoteClient)
        {
            // sendRegionHandshakeToAll();
        }

        public void setRegionTerrainSettings(float WaterHeight,
                float TerrainRaiseLimit, float TerrainLowerLimit,
                bool UseEstateSun, bool UseFixedSun, float SunHour,
                bool UseGlobal, bool EstateFixedSun, float EstateSunHour)
        {
            // Water Height
            Scene.RegionInfo.RegionSettings.WaterHeight = WaterHeight;

            // Terraforming limits
            Scene.RegionInfo.RegionSettings.TerrainRaiseLimit = TerrainRaiseLimit;
            Scene.RegionInfo.RegionSettings.TerrainLowerLimit = TerrainLowerLimit;

            // Time of day / fixed sun
            Scene.RegionInfo.RegionSettings.UseEstateSun = UseEstateSun;
            Scene.RegionInfo.RegionSettings.FixedSun = UseFixedSun;
            Scene.RegionInfo.RegionSettings.SunPosition = SunHour;

            Scene.TriggerEstateSunUpdate();

            //m_log.Debug("[ESTATE]: UFS: " + UseFixedSun.ToString());
            //m_log.Debug("[ESTATE]: SunHour: " + SunHour.ToString());

            sendRegionInfoPacketToAll();
            Scene.RegionInfo.RegionSettings.Save();
            TriggerRegionInfoChange();
        }

        private void handleEstateRestartSimRequest(IClientAPI remoteClient, int timeInSeconds)
        {
            if (!AllowRegionRestartFromClient)
            {
                remoteClient.SendAlertMessage("Region restart has been disabled on this simulator.");
                return;
            }

            IRestartModule restartModule = Scene.RequestModuleInterface<IRestartModule>();
            if (restartModule != null)
            {
                List<int> times = new List<int>();
                while (timeInSeconds > 0)
                {
                    times.Add(timeInSeconds);
                    if (timeInSeconds > 300)
                        timeInSeconds -= 120;
                    else if (timeInSeconds > 30)
                        timeInSeconds -= 30;
                    else
                        timeInSeconds -= 15;
                }

                restartModule.ScheduleRestart(UUID.Zero, "Region will restart in {0}", times.ToArray(), false);

                m_log.InfoFormat(
                    "User {0} requested restart of region {1} in {2} seconds", 
                    remoteClient.Name, Scene.Name, times.Count != 0 ? times[0] : 0);
            }
        }

        private void handleChangeEstateCovenantRequest(IClientAPI remoteClient, UUID estateCovenantID)
        {
//            m_log.DebugFormat(
//                "[ESTATE MANAGEMENT MODULE]: Handling request from {0} to change estate covenant to {1}", 
//                remoteClient.Name, estateCovenantID);
            
            Scene.RegionInfo.RegionSettings.Covenant = estateCovenantID;
            Scene.RegionInfo.RegionSettings.CovenantChangedDateTime = Util.UnixTimeSinceEpoch();
            Scene.RegionInfo.RegionSettings.Save();
            TriggerRegionInfoChange();
        }

        private void handleEstateAccessDeltaRequest(IClientAPI remote_client, UUID invoice, int estateAccessType, UUID user)
        {
            // EstateAccessDelta handles Estate Managers, Sim Access, Sim Banlist, allowed Groups..  etc.

            if (user == Scene.RegionInfo.EstateSettings.EstateOwner)
                return; // never process EO

            if ((estateAccessType & 4) != 0) // User add
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, true))
                {
                    if ((estateAccessType & 1) != 0) // All estates
                    {
                        List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                        EstateSettings estateSettings;

                        foreach (int estateID in estateIDs)
                        {
                            if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                            {
                                estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                estateSettings.AddEstateUser(user);
                                estateSettings.Save();
                            }
                        }
                    }

                    Scene.RegionInfo.EstateSettings.AddEstateUser(user);
                    Scene.RegionInfo.EstateSettings.Save();

                    TriggerEstateInfoChange();
                    remote_client.SendEstateList(invoice, (int)Constants.EstateAccessCodex.AccessOptions, Scene.RegionInfo.EstateSettings.EstateAccess, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }

            }

            if ((estateAccessType & 8) != 0) // User remove
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, true))
                {
                    if ((estateAccessType & 1) != 0) // All estates
                    {
                        List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                        EstateSettings estateSettings;

                        foreach (int estateID in estateIDs)
                        {
                            if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                            {
                                estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                estateSettings.RemoveEstateUser(user);
                                estateSettings.Save();
                            }
                        }
                    }

                    Scene.RegionInfo.EstateSettings.RemoveEstateUser(user);
                    Scene.RegionInfo.EstateSettings.Save();

                    TriggerEstateInfoChange();
                    remote_client.SendEstateList(invoice, (int)Constants.EstateAccessCodex.AccessOptions, Scene.RegionInfo.EstateSettings.EstateAccess, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }

            if ((estateAccessType & 16) != 0) // Group add
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, true))
                {
                    if ((estateAccessType & 1) != 0) // All estates
                    {
                        List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                        EstateSettings estateSettings;

                        foreach (int estateID in estateIDs)
                        {
                            if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                            {
                                estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                estateSettings.AddEstateGroup(user);
                                estateSettings.Save();
                            }
                        }
                    }

                    Scene.RegionInfo.EstateSettings.AddEstateGroup(user);
                    Scene.RegionInfo.EstateSettings.Save();

                    TriggerEstateInfoChange();
                    remote_client.SendEstateList(invoice, (int)Constants.EstateAccessCodex.AllowedGroups, Scene.RegionInfo.EstateSettings.EstateGroups, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }

            if ((estateAccessType & 32) != 0) // Group remove
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, true))
                {
                    if ((estateAccessType & 1) != 0) // All estates
                    {
                        List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                        EstateSettings estateSettings;

                        foreach (int estateID in estateIDs)
                        {
                            if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                            {
                                estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                estateSettings.RemoveEstateGroup(user);
                                estateSettings.Save();
                            }
                        }
                    }

                    Scene.RegionInfo.EstateSettings.RemoveEstateGroup(user);
                    Scene.RegionInfo.EstateSettings.Save();

                    TriggerEstateInfoChange();
                    remote_client.SendEstateList(invoice, (int)Constants.EstateAccessCodex.AllowedGroups, Scene.RegionInfo.EstateSettings.EstateGroups, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }

            if ((estateAccessType & 64) != 0) // Ban add
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, false))
                {
                    EstateBan[] banlistcheck = Scene.RegionInfo.EstateSettings.EstateBans;

                    bool alreadyInList = false;

                    for (int i = 0; i < banlistcheck.Length; i++)
                    {
                        if (user == banlistcheck[i].BannedUserID)
                        {
                            alreadyInList = true;
                            break;
                        }

                    }
                    if (!alreadyInList)
                    {

                        if ((estateAccessType & 1) != 0) // All estates
                        {
                            List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                            EstateSettings estateSettings;

                            foreach (int estateID in estateIDs)
                            {
                                if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                                {
                                    EstateBan bitem = new EstateBan();

                                    bitem.BannedUserID = user;
                                    bitem.EstateID = (uint)estateID;
                                    bitem.BannedHostAddress = "0.0.0.0";
                                    bitem.BannedHostIPMask = "0.0.0.0";

                                    estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                    estateSettings.AddBan(bitem);
                                    estateSettings.Save();
                                }
                            }
                        }

                        EstateBan item = new EstateBan();

                        item.BannedUserID = user;
                        item.EstateID = Scene.RegionInfo.EstateSettings.EstateID;
                        item.BannedHostAddress = "0.0.0.0";
                        item.BannedHostIPMask = "0.0.0.0";

                        Scene.RegionInfo.EstateSettings.AddBan(item);
                        Scene.RegionInfo.EstateSettings.Save();

                        TriggerEstateInfoChange();

                        ScenePresence s = Scene.GetScenePresence(user);
                        if (s != null)
                        {
                            if (!s.IsChildAgent)
                            {
                                if (!Scene.TeleportClientHome(user, s.ControllingClient))
                                {
                                    s.ControllingClient.Kick("Your access to the region was revoked and TP home failed - you have been logged out.");
                                    Scene.CloseAgent(s.UUID, false);
                                }
                            }
                        }

                    }
                    else
                    {
                        remote_client.SendAlertMessage("User is already on the region ban list");
                    }
                    //Scene.RegionInfo.regionBanlist.Add(Manager(user);
                    remote_client.SendBannedUserList(invoice, Scene.RegionInfo.EstateSettings.EstateBans, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }

            if ((estateAccessType & 128) != 0) // Ban remove
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, false))
                {
                    EstateBan[] banlistcheck = Scene.RegionInfo.EstateSettings.EstateBans;

                    bool alreadyInList = false;
                    EstateBan listitem = null;

                    for (int i = 0; i < banlistcheck.Length; i++)
                    {
                        if (user == banlistcheck[i].BannedUserID)
                        {
                            alreadyInList = true;
                            listitem = banlistcheck[i];
                            break;
                        }
                    }
                    
                    if (alreadyInList && listitem != null)
                    {
                        if ((estateAccessType & 1) != 0) // All estates
                        {
                            List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                            EstateSettings estateSettings;

                            foreach (int estateID in estateIDs)
                            {
                                if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                                {
                                    estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                    estateSettings.RemoveBan(user);
                                    estateSettings.Save();
                                }
                            }
                        }

                        Scene.RegionInfo.EstateSettings.RemoveBan(listitem.BannedUserID);
                        Scene.RegionInfo.EstateSettings.Save();

                        TriggerEstateInfoChange();
                    }
                    else
                    {
                        remote_client.SendAlertMessage("User is not on the region ban list");
                    }
                    
                    //Scene.RegionInfo.regionBanlist.Add(Manager(user);
                    remote_client.SendBannedUserList(invoice, Scene.RegionInfo.EstateSettings.EstateBans, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }

            if ((estateAccessType & 256) != 0) // Manager add
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, true))
                {
                    if ((estateAccessType & 1) != 0) // All estates
                    {
                        List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                        EstateSettings estateSettings;

                        foreach (int estateID in estateIDs)
                        {
                            if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                            {
                                estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                estateSettings.AddEstateManager(user);
                                estateSettings.Save();
                            }
                        }
                    }

                    Scene.RegionInfo.EstateSettings.AddEstateManager(user);
                    Scene.RegionInfo.EstateSettings.Save();

                    TriggerEstateInfoChange();
                    remote_client.SendEstateList(invoice, (int)Constants.EstateAccessCodex.EstateManagers, Scene.RegionInfo.EstateSettings.EstateManagers, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }

            if ((estateAccessType & 512) != 0) // Manager remove
            {
                if (Scene.Permissions.CanIssueEstateCommand(remote_client.AgentId, true))
                {
                    if ((estateAccessType & 1) != 0) // All estates
                    {
                        List<int> estateIDs = Scene.EstateDataService.GetEstatesByOwner(Scene.RegionInfo.EstateSettings.EstateOwner);
                        EstateSettings estateSettings;

                        foreach (int estateID in estateIDs)
                        {
                            if (estateID != Scene.RegionInfo.EstateSettings.EstateID)
                            {
                                estateSettings = Scene.EstateDataService.LoadEstateSettings(estateID);
                                estateSettings.RemoveEstateManager(user);
                                estateSettings.Save();
                            }
                        }
                    }

                    Scene.RegionInfo.EstateSettings.RemoveEstateManager(user);
                    Scene.RegionInfo.EstateSettings.Save();

                    TriggerEstateInfoChange();
                    remote_client.SendEstateList(invoice, (int)Constants.EstateAccessCodex.EstateManagers, Scene.RegionInfo.EstateSettings.EstateManagers, Scene.RegionInfo.EstateSettings.EstateID);
                }
                else
                {
                    remote_client.SendAlertMessage("Method EstateAccessDelta Failed, you don't have permissions");
                }
            }
        }

        public void HandleOnEstateManageTelehub(IClientAPI client, UUID invoice, UUID senderID, string cmd, uint param1)
        {
            SceneObjectPart part;

            switch (cmd)
            {
                case "info ui":
                    break;

                case "connect":
                    // Add the Telehub
                    part = Scene.GetSceneObjectPart((uint)param1);
                    if (part == null)
                        return;
                    SceneObjectGroup grp = part.ParentGroup;

                    m_Telehub.Connect(grp);
                    break;

                case "delete":
                    // Disconnect Telehub
                    m_Telehub.Disconnect();
                    break;

                case "spawnpoint add":
                    // Add SpawnPoint to the Telehub
                    part = Scene.GetSceneObjectPart((uint)param1);
                    if (part == null)
                        return;
                    m_Telehub.AddSpawnPoint(part.AbsolutePosition);
                    break;

                case "spawnpoint remove":
                    // Remove SpawnPoint from Telehub
                    m_Telehub.RemoveSpawnPoint((int)param1);
                    break;

                default:
                    break;
            }

            if (client != null)
                SendTelehubInfo(client);
        }

        private void SendSimulatorBlueBoxMessage(
            IClientAPI remote_client, UUID invoice, UUID senderID, UUID sessionID, string senderName, string message)
        {
            IDialogModule dm = Scene.RequestModuleInterface<IDialogModule>();
            
            if (dm != null)
                dm.SendNotificationToUsersInRegion(senderID, senderName, message);
        }

        private void SendEstateBlueBoxMessage(
            IClientAPI remote_client, UUID invoice, UUID senderID, UUID sessionID, string senderName, string message)
        {
            TriggerEstateMessage(senderID, senderName, message);
        }

        private void handleEstateDebugRegionRequest(
            IClientAPI remote_client, UUID invoice, UUID senderID,
            bool disableScripts, bool disableCollisions, bool disablePhysics)
        {
            Scene.RegionInfo.RegionSettings.DisablePhysics = disablePhysics;
            Scene.RegionInfo.RegionSettings.DisableScripts = disableScripts;
            Scene.RegionInfo.RegionSettings.DisableCollisions = disableCollisions;
            Scene.RegionInfo.RegionSettings.Save();
            TriggerRegionInfoChange();

            ISceneCommandsModule scm = Scene.RequestModuleInterface<ISceneCommandsModule>();

            if (scm != null)
            {
                scm.SetSceneDebugOptions(
                    new Dictionary<string, string>() {
                        { "scripting", (!disableScripts).ToString() },
                        { "collisions", (!disableCollisions).ToString() },
                        { "physics", (!disablePhysics).ToString() }
                    }
                );
            }
        }

        private void handleEstateTeleportOneUserHomeRequest(IClientAPI remover_client, UUID invoice, UUID senderID, UUID prey)
        {
            if (!Scene.Permissions.CanIssueEstateCommand(remover_client.AgentId, false))
                return;

            if (prey != UUID.Zero)
            {
                ScenePresence s = Scene.GetScenePresence(prey);
                if (s != null)
                {
                    if (!Scene.TeleportClientHome(prey, s.ControllingClient))
                    {
                        s.ControllingClient.Kick("You were teleported home by the region owner, but the TP failed - you have been logged out.");
                        Scene.CloseAgent(s.UUID, false);
                    }
                }
            }
        }

        private void handleEstateTeleportAllUsersHomeRequest(IClientAPI remover_client, UUID invoice, UUID senderID)
        {
            if (!Scene.Permissions.CanIssueEstateCommand(remover_client.AgentId, false))
                return;

            Scene.ForEachRootClient(delegate(IClientAPI client)
            {
                if (client.AgentId != senderID)
                {
                    // make sure they are still there, we could be working down a long list
                    // Also make sure they are actually in the region
                    ScenePresence p;
                    if(Scene.TryGetScenePresence(client.AgentId, out p))
                    {
                        if (!Scene.TeleportClientHome(p.UUID, p.ControllingClient))
                        {
                            p.ControllingClient.Kick("You were teleported home by the region owner, but the TP failed - you have been logged out.");
                            Scene.CloseAgent(p.UUID, false);
                        }
                    }
                }
            });
        }
        
        private void AbortTerrainXferHandler(IClientAPI remoteClient, ulong XferID)
        {
            if (TerrainUploader != null)
            {
                lock (TerrainUploader)
                {
                    if (XferID == TerrainUploader.XferID)
                    {
                        remoteClient.OnXferReceive -= TerrainUploader.XferReceive;
                        remoteClient.OnAbortXfer -= AbortTerrainXferHandler;
                        TerrainUploader.TerrainUploadDone -= HandleTerrainApplication;

                        TerrainUploader = null;
                        remoteClient.SendAlertMessage("Terrain Upload aborted by the client");
                    }
                }
            }

        }
        private void HandleTerrainApplication(string filename, byte[] terrainData, IClientAPI remoteClient)
        {
            lock (TerrainUploader)
            {
                remoteClient.OnXferReceive -= TerrainUploader.XferReceive;
                remoteClient.OnAbortXfer -= AbortTerrainXferHandler;
                TerrainUploader.TerrainUploadDone -= HandleTerrainApplication;

                TerrainUploader = null;
            }
            remoteClient.SendAlertMessage("Terrain Upload Complete. Loading....");
            ITerrainModule terr = Scene.RequestModuleInterface<ITerrainModule>();

            if (terr != null)
            {
                m_log.Warn("[CLIENT]: Got Request to Send Terrain in region " + Scene.RegionInfo.RegionName);

                try
                {
                    MemoryStream terrainStream = new MemoryStream(terrainData);
                    terr.LoadFromStream(filename, terrainStream);
                    terrainStream.Close();

                    FileInfo x = new FileInfo(filename);
                    remoteClient.SendAlertMessage("Your terrain was loaded as a " + x.Extension + " file. It may take a few moments to appear.");
                }
                catch (IOException e)
                {
                    m_log.ErrorFormat("[TERRAIN]: Error Saving a terrain file uploaded via the estate tools.  It gave us the following error: {0}", e.ToString());
                    remoteClient.SendAlertMessage("There was an IO Exception loading your terrain.  Please check free space.");

                    return;
                }
                catch (SecurityException e)
                {
                    m_log.ErrorFormat("[TERRAIN]: Error Saving a terrain file uploaded via the estate tools.  It gave us the following error: {0}", e.ToString());
                    remoteClient.SendAlertMessage("There was a security Exception loading your terrain.  Please check the security on the simulator drive");

                    return;
                }
                catch (UnauthorizedAccessException e)
                {
                    m_log.ErrorFormat("[TERRAIN]: Error Saving a terrain file uploaded via the estate tools.  It gave us the following error: {0}", e.ToString());
                    remoteClient.SendAlertMessage("There was a security Exception loading your terrain.  Please check the security on the simulator drive");

                    return;
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[TERRAIN]: Error loading a terrain file uploaded via the estate tools.  It gave us the following error: {0}", e.ToString());
                    remoteClient.SendAlertMessage("There was a general error loading your terrain.  Please fix the terrain file and try again");
                }
            }
            else
            {
                remoteClient.SendAlertMessage("Unable to apply terrain.  Cannot get an instance of the terrain module");
            }
        }

        private void handleUploadTerrain(IClientAPI remote_client, string clientFileName)
        {
            if (TerrainUploader == null)
            {

                TerrainUploader = new EstateTerrainXferHandler(remote_client, clientFileName);
                lock (TerrainUploader)
                {
                    remote_client.OnXferReceive += TerrainUploader.XferReceive;
                    remote_client.OnAbortXfer += AbortTerrainXferHandler;
                    TerrainUploader.TerrainUploadDone += HandleTerrainApplication;
                }
                TerrainUploader.RequestStartXfer(remote_client);

            }
            else
            {
                remote_client.SendAlertMessage("Another Terrain Upload is in progress.  Please wait your turn!");
            }
        }
        
        private void handleTerrainRequest(IClientAPI remote_client, string clientFileName)
        {
            // Save terrain here
            ITerrainModule terr = Scene.RequestModuleInterface<ITerrainModule>();
            
            if (terr != null)
            {
                m_log.Warn("[CLIENT]: Got Request to Send Terrain in region " + Scene.RegionInfo.RegionName);
                if (File.Exists(Util.dataDir() + "/terrain.raw"))
                {
                    File.Delete(Util.dataDir() + "/terrain.raw");
                }
                terr.SaveToFile(Util.dataDir() + "/terrain.raw");

                FileStream input = new FileStream(Util.dataDir() + "/terrain.raw", FileMode.Open);
                byte[] bdata = new byte[input.Length];
                input.Read(bdata, 0, (int)input.Length);
                remote_client.SendAlertMessage("Terrain file written, starting download...");
                Scene.XferManager.AddNewFile("terrain.raw", bdata);
                // Tell client about it
                m_log.Warn("[CLIENT]: Sending Terrain to " + remote_client.Name);
                remote_client.SendInitiateDownload("terrain.raw", clientFileName);
            }
        }

        private void HandleRegionInfoRequest(IClientAPI remote_client)
        {
           RegionInfoForEstateMenuArgs args = new RegionInfoForEstateMenuArgs();
           args.billableFactor = Scene.RegionInfo.EstateSettings.BillableFactor;
           args.estateID = Scene.RegionInfo.EstateSettings.EstateID;
           args.maxAgents = (byte)Scene.RegionInfo.RegionSettings.AgentLimit;
           args.objectBonusFactor = (float)Scene.RegionInfo.RegionSettings.ObjectBonus;
           args.parentEstateID = Scene.RegionInfo.EstateSettings.ParentEstateID;
           args.pricePerMeter = Scene.RegionInfo.EstateSettings.PricePerMeter;
           args.redirectGridX = Scene.RegionInfo.EstateSettings.RedirectGridX;
           args.redirectGridY = Scene.RegionInfo.EstateSettings.RedirectGridY;
           args.regionFlags = GetRegionFlags();
           args.simAccess = Scene.RegionInfo.AccessLevel;
           args.sunHour = (float)Scene.RegionInfo.RegionSettings.SunPosition;
           args.terrainLowerLimit = (float)Scene.RegionInfo.RegionSettings.TerrainLowerLimit;
           args.terrainRaiseLimit = (float)Scene.RegionInfo.RegionSettings.TerrainRaiseLimit;
           args.useEstateSun = Scene.RegionInfo.RegionSettings.UseEstateSun;
           args.waterHeight = (float)Scene.RegionInfo.RegionSettings.WaterHeight;
           args.simName = Scene.RegionInfo.RegionName;
           args.regionType = Scene.RegionInfo.RegionType;

           remote_client.SendRegionInfoToEstateMenu(args);
        }

        private void HandleEstateCovenantRequest(IClientAPI remote_client)
        {
            remote_client.SendEstateCovenantInformation(Scene.RegionInfo.RegionSettings.Covenant);
        }

        private void HandleLandStatRequest(int parcelID, uint reportType, uint requestFlags, string filter, IClientAPI remoteClient)
        {
            if (!Scene.Permissions.CanIssueEstateCommand(remoteClient.AgentId, false))
                return;

            Dictionary<uint, float> sceneData = null;

            if (reportType == 1)
            {
                sceneData = Scene.PhysicsScene.GetTopColliders();
            }
            else if (reportType == 0)
            {
                IScriptModule scriptModule = Scene.RequestModuleInterface<IScriptModule>();

                if (scriptModule != null)
                    sceneData = scriptModule.GetObjectScriptsExecutionTimes();
            }

            List<LandStatReportItem> SceneReport = new List<LandStatReportItem>();
            if (sceneData != null)
            {
                var sortedSceneData
                    = sceneData.Select(
                        item => new { Measurement = item.Value, Part = Scene.GetSceneObjectPart(item.Key) });

                sortedSceneData.OrderBy(item => item.Measurement);

                int items = 0;

                foreach (var entry in sortedSceneData)
                {
                    // The object may have been deleted since we received the data.
                    if (entry.Part == null)
                        continue;

                    // Don't show scripts that haven't executed or where execution time is below one microsecond in
                    // order to produce a more readable report.
                    if (entry.Measurement < 0.001)
                        continue;

                    items++;
                    SceneObjectGroup so = entry.Part.ParentGroup;

                    LandStatReportItem lsri = new LandStatReportItem();
                    lsri.LocationX = so.AbsolutePosition.X;
                    lsri.LocationY = so.AbsolutePosition.Y;
                    lsri.LocationZ = so.AbsolutePosition.Z;
                    lsri.Score = entry.Measurement;
                    lsri.TaskID = so.UUID;
                    lsri.TaskLocalID = so.LocalId;
                    lsri.TaskName = entry.Part.Name;
                    lsri.OwnerName = UserManager.GetUserName(so.OwnerID);

                    if (filter.Length != 0)
                    {
                        if ((lsri.OwnerName.Contains(filter) || lsri.TaskName.Contains(filter)))
                        {
                        }
                        else
                        {
                            continue;
                        }
                    }

                    SceneReport.Add(lsri);

                    if (items >= 100)
                        break;
                }
            }

            remoteClient.SendLandStatReply(reportType, requestFlags, (uint)SceneReport.Count,SceneReport.ToArray());
        }

        #endregion

        #region Outgoing Packets

        public void sendRegionInfoPacketToAll()
        {
            Scene.ForEachRootClient(delegate(IClientAPI client)
            {
                HandleRegionInfoRequest(client);
            });
        }

        public void sendRegionHandshake(IClientAPI remoteClient)
        {
            RegionHandshakeArgs args = new RegionHandshakeArgs();

            args.isEstateManager = Scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(remoteClient.AgentId);
            if (Scene.RegionInfo.EstateSettings.EstateOwner != UUID.Zero && Scene.RegionInfo.EstateSettings.EstateOwner == remoteClient.AgentId)
                args.isEstateManager = true;

            args.billableFactor = Scene.RegionInfo.EstateSettings.BillableFactor;
            args.terrainStartHeight0 = (float)Scene.RegionInfo.RegionSettings.Elevation1SW;
            args.terrainHeightRange0 = (float)Scene.RegionInfo.RegionSettings.Elevation2SW;
            args.terrainStartHeight1 = (float)Scene.RegionInfo.RegionSettings.Elevation1NW;
            args.terrainHeightRange1 = (float)Scene.RegionInfo.RegionSettings.Elevation2NW;
            args.terrainStartHeight2 = (float)Scene.RegionInfo.RegionSettings.Elevation1SE;
            args.terrainHeightRange2 = (float)Scene.RegionInfo.RegionSettings.Elevation2SE;
            args.terrainStartHeight3 = (float)Scene.RegionInfo.RegionSettings.Elevation1NE;
            args.terrainHeightRange3 = (float)Scene.RegionInfo.RegionSettings.Elevation2NE;
            args.simAccess = Scene.RegionInfo.AccessLevel;
            args.waterHeight = (float)Scene.RegionInfo.RegionSettings.WaterHeight;
            args.regionFlags = GetRegionFlags();
            args.regionName = Scene.RegionInfo.RegionName;
            args.SimOwner = Scene.RegionInfo.EstateSettings.EstateOwner;

            args.terrainBase0 = UUID.Zero;
            args.terrainBase1 = UUID.Zero;
            args.terrainBase2 = UUID.Zero;
            args.terrainBase3 = UUID.Zero;
            args.terrainDetail0 = Scene.RegionInfo.RegionSettings.TerrainTexture1;
            args.terrainDetail1 = Scene.RegionInfo.RegionSettings.TerrainTexture2;
            args.terrainDetail2 = Scene.RegionInfo.RegionSettings.TerrainTexture3;
            args.terrainDetail3 = Scene.RegionInfo.RegionSettings.TerrainTexture4;

//            m_log.DebugFormat("[ESTATE MANAGEMENT MODULE]: Sending terrain texture 1 {0} for region {1}", args.terrainDetail0, Scene.RegionInfo.RegionName);
//            m_log.DebugFormat("[ESTATE MANAGEMENT MODULE]: Sending terrain texture 2 {0} for region {1}", args.terrainDetail1, Scene.RegionInfo.RegionName);
//            m_log.DebugFormat("[ESTATE MANAGEMENT MODULE]: Sending terrain texture 3 {0} for region {1}", args.terrainDetail2, Scene.RegionInfo.RegionName);
//            m_log.DebugFormat("[ESTATE MANAGEMENT MODULE]: Sending terrain texture 4 {0} for region {1}", args.terrainDetail3, Scene.RegionInfo.RegionName);

            remoteClient.SendRegionHandshake(Scene.RegionInfo,args);
        }

        public void sendRegionHandshakeToAll()
        {
            Scene.ForEachClient(sendRegionHandshake);
        }

        public void handleEstateChangeInfo(IClientAPI remoteClient, UUID invoice, UUID senderID, UInt32 parms1, UInt32 parms2)
        {
            if (parms2 == 0)
            {
                Scene.RegionInfo.EstateSettings.UseGlobalTime = true;
                Scene.RegionInfo.EstateSettings.SunPosition = 0.0;
            }
            else
            {
                Scene.RegionInfo.EstateSettings.UseGlobalTime = false;
                Scene.RegionInfo.EstateSettings.SunPosition = (parms2 - 0x1800)/1024.0;
                // Warning: FixedSun should be set to True, otherwise this sun position won't be used.
            }

            if ((parms1 & 0x00000010) != 0)
                Scene.RegionInfo.EstateSettings.FixedSun = true;
            else
                Scene.RegionInfo.EstateSettings.FixedSun = false;

            if ((parms1 & 0x00008000) != 0)
                Scene.RegionInfo.EstateSettings.PublicAccess = true;
            else
                Scene.RegionInfo.EstateSettings.PublicAccess = false;

            if ((parms1 & 0x10000000) != 0)
                Scene.RegionInfo.EstateSettings.AllowVoice = true;
            else
                Scene.RegionInfo.EstateSettings.AllowVoice = false;

            if ((parms1 & 0x00100000) != 0)
                Scene.RegionInfo.EstateSettings.AllowDirectTeleport = true;
            else
                Scene.RegionInfo.EstateSettings.AllowDirectTeleport = false;

            if ((parms1 & 0x00800000) != 0)
                Scene.RegionInfo.EstateSettings.DenyAnonymous = true;
            else
                Scene.RegionInfo.EstateSettings.DenyAnonymous = false;

            if ((parms1 & 0x01000000) != 0)
                Scene.RegionInfo.EstateSettings.DenyIdentified = true;
            else
                Scene.RegionInfo.EstateSettings.DenyIdentified = false;

            if ((parms1 & 0x02000000) != 0)
                Scene.RegionInfo.EstateSettings.DenyTransacted = true;
            else
                Scene.RegionInfo.EstateSettings.DenyTransacted = false;

            if ((parms1 & 0x40000000) != 0)
                Scene.RegionInfo.EstateSettings.DenyMinors = true;
            else
                Scene.RegionInfo.EstateSettings.DenyMinors = false;

            Scene.RegionInfo.EstateSettings.Save();
            TriggerEstateInfoChange();

            Scene.TriggerEstateSunUpdate();

            sendDetailedEstateData(remoteClient, invoice);
        }

        #endregion

        #region Other Functions

        public void changeWaterHeight(float height)
        {
            setRegionTerrainSettings(height,
                    (float)Scene.RegionInfo.RegionSettings.TerrainRaiseLimit,
                    (float)Scene.RegionInfo.RegionSettings.TerrainLowerLimit,
                    Scene.RegionInfo.RegionSettings.UseEstateSun,
                    Scene.RegionInfo.RegionSettings.FixedSun,
                    (float)Scene.RegionInfo.RegionSettings.SunPosition,
                    Scene.RegionInfo.EstateSettings.UseGlobalTime,
                    Scene.RegionInfo.EstateSettings.FixedSun,
                    (float)Scene.RegionInfo.EstateSettings.SunPosition);

            sendRegionInfoPacketToAll();
        }

        #endregion

        private void EventManager_OnNewClient(IClientAPI client)
        {
            client.OnDetailedEstateDataRequest += clientSendDetailedEstateData;
            client.OnSetEstateFlagsRequest += estateSetRegionInfoHandler;
//            client.OnSetEstateTerrainBaseTexture += setEstateTerrainBaseTexture;
            client.OnSetEstateTerrainDetailTexture += setEstateTerrainBaseTexture;
            client.OnSetEstateTerrainTextureHeights += setEstateTerrainTextureHeights;
            client.OnCommitEstateTerrainTextureRequest += handleCommitEstateTerrainTextureRequest;
            client.OnSetRegionTerrainSettings += setRegionTerrainSettings;
            client.OnEstateRestartSimRequest += handleEstateRestartSimRequest;
            client.OnEstateChangeCovenantRequest += handleChangeEstateCovenantRequest;
            client.OnEstateChangeInfo += handleEstateChangeInfo;
            client.OnEstateManageTelehub += HandleOnEstateManageTelehub;
            client.OnUpdateEstateAccessDeltaRequest += handleEstateAccessDeltaRequest;
            client.OnSimulatorBlueBoxMessageRequest += SendSimulatorBlueBoxMessage;
            client.OnEstateBlueBoxMessageRequest += SendEstateBlueBoxMessage;
            client.OnEstateDebugRegionRequest += handleEstateDebugRegionRequest;
            client.OnEstateTeleportOneUserHomeRequest += handleEstateTeleportOneUserHomeRequest;
            client.OnEstateTeleportAllUsersHomeRequest += handleEstateTeleportAllUsersHomeRequest;
            client.OnRequestTerrain += handleTerrainRequest;
            client.OnUploadTerrain += handleUploadTerrain;

            client.OnRegionInfoRequest += HandleRegionInfoRequest;
            client.OnEstateCovenantRequest += HandleEstateCovenantRequest;
            client.OnLandStatRequest += HandleLandStatRequest;
            sendRegionHandshake(client);
        }

        public uint GetRegionFlags()
        {
            RegionFlags flags = RegionFlags.None;

            // Fully implemented
            //
            if (Scene.RegionInfo.RegionSettings.AllowDamage)
                flags |= RegionFlags.AllowDamage;
            if (Scene.RegionInfo.RegionSettings.BlockTerraform)
                flags |= RegionFlags.BlockTerraform;
            if (!Scene.RegionInfo.RegionSettings.AllowLandResell)
                flags |= RegionFlags.BlockLandResell;
            if (Scene.RegionInfo.RegionSettings.DisableCollisions)
                flags |= RegionFlags.SkipCollisions;
            if (Scene.RegionInfo.RegionSettings.DisableScripts)
                flags |= RegionFlags.SkipScripts;
            if (Scene.RegionInfo.RegionSettings.DisablePhysics)
                flags |= RegionFlags.SkipPhysics;
            if (Scene.RegionInfo.RegionSettings.BlockFly)
                flags |= RegionFlags.NoFly;
            if (Scene.RegionInfo.RegionSettings.RestrictPushing)
                flags |= RegionFlags.RestrictPushObject;
            if (Scene.RegionInfo.RegionSettings.AllowLandJoinDivide)
                flags |= RegionFlags.AllowParcelChanges;
            if (Scene.RegionInfo.RegionSettings.BlockShowInSearch)
                flags |= RegionFlags.BlockParcelSearch;

            if (Scene.RegionInfo.RegionSettings.FixedSun)
                flags |= RegionFlags.SunFixed;
            if (Scene.RegionInfo.RegionSettings.Sandbox)
                flags |= RegionFlags.Sandbox;
            if (Scene.RegionInfo.EstateSettings.AllowVoice)
                flags |= RegionFlags.AllowVoice;
            if (Scene.RegionInfo.EstateSettings.AllowLandmark)
                flags |= RegionFlags.AllowLandmark;
            if (Scene.RegionInfo.EstateSettings.AllowSetHome)
                flags |= RegionFlags.AllowSetHome;
            if (Scene.RegionInfo.EstateSettings.BlockDwell)
                flags |= RegionFlags.BlockDwell;
            if (Scene.RegionInfo.EstateSettings.ResetHomeOnTeleport)
                flags |= RegionFlags.ResetHomeOnTeleport;


            // TODO: SkipUpdateInterestList

            // Omitted
            //
            // Omitted: NullLayer (what is that?)
            // Omitted: SkipAgentAction (what does it do?)

            return (uint)flags;
        }

        public uint GetEstateFlags()
        {
            RegionFlags flags = RegionFlags.None;

            if (Scene.RegionInfo.EstateSettings.FixedSun)
                flags |= RegionFlags.SunFixed;
            if (Scene.RegionInfo.EstateSettings.PublicAccess)
                flags |= (RegionFlags.PublicAllowed |
                          RegionFlags.ExternallyVisible);
            if (Scene.RegionInfo.EstateSettings.AllowVoice)
                flags |= RegionFlags.AllowVoice;
            if (Scene.RegionInfo.EstateSettings.AllowDirectTeleport)
                flags |= RegionFlags.AllowDirectTeleport;
            if (Scene.RegionInfo.EstateSettings.DenyAnonymous)
                flags |= RegionFlags.DenyAnonymous;
            if (Scene.RegionInfo.EstateSettings.DenyIdentified)
                flags |= RegionFlags.DenyIdentified;
            if (Scene.RegionInfo.EstateSettings.DenyTransacted)
                flags |= RegionFlags.DenyTransacted;
            if (Scene.RegionInfo.EstateSettings.AbuseEmailToEstateOwner)
                flags |= RegionFlags.AbuseEmailToEstateOwner;
            if (Scene.RegionInfo.EstateSettings.BlockDwell)
                flags |= RegionFlags.BlockDwell;
            if (Scene.RegionInfo.EstateSettings.EstateSkipScripts)
                flags |= RegionFlags.EstateSkipScripts;
            if (Scene.RegionInfo.EstateSettings.ResetHomeOnTeleport)
                flags |= RegionFlags.ResetHomeOnTeleport;
            if (Scene.RegionInfo.EstateSettings.TaxFree)
                flags |= RegionFlags.TaxFree;
            if (Scene.RegionInfo.EstateSettings.AllowLandmark)
                flags |= RegionFlags.AllowLandmark;
            if (Scene.RegionInfo.EstateSettings.AllowParcelChanges)
                flags |= RegionFlags.AllowParcelChanges;
            if (Scene.RegionInfo.EstateSettings.AllowSetHome)
                flags |= RegionFlags.AllowSetHome;
            if (Scene.RegionInfo.EstateSettings.DenyMinors)
                flags |= (RegionFlags)(1 << 30);

            return (uint)flags;
        }

        public bool IsManager(UUID avatarID)
        {
            if (avatarID == Scene.RegionInfo.EstateSettings.EstateOwner)
                return true;

            List<UUID> ems = new List<UUID>(Scene.RegionInfo.EstateSettings.EstateManagers);
            if (ems.Contains(avatarID))
                return true;

            return false;
        }

        public void TriggerRegionInfoChange()
        {
            m_regionChangeTimer.Stop();
            m_regionChangeTimer.Start();
        }

        protected void RaiseRegionInfoChange(object sender, ElapsedEventArgs e)
        {
            ChangeDelegate change = OnRegionInfoChange;

            if (change != null)
                change(Scene.RegionInfo.RegionID);
        }

        public void TriggerEstateInfoChange()
        {
            ChangeDelegate change = OnEstateInfoChange;

            if (change != null)
                change(Scene.RegionInfo.RegionID);
        }

        public void TriggerEstateMessage(UUID fromID, string fromName, string message)
        {
            MessageDelegate onmessage = OnEstateMessage;

            if (onmessage != null)
                onmessage(Scene.RegionInfo.RegionID, fromID, fromName, message);
        }


        private void SendTelehubInfo(IClientAPI client)
        {
            RegionSettings settings =
                    this.Scene.RegionInfo.RegionSettings;

            SceneObjectGroup telehub = null;
            if (settings.TelehubObject != UUID.Zero &&
                (telehub = Scene.GetSceneObjectGroup(settings.TelehubObject)) != null)
            {
                List<Vector3> spawnPoints = new List<Vector3>();

                foreach (SpawnPoint sp in settings.SpawnPoints())
                {
                    spawnPoints.Add(sp.GetLocation(Vector3.Zero, Quaternion.Identity));
                }

                client.SendTelehubInfo(settings.TelehubObject,
                                       telehub.Name,
                                       telehub.AbsolutePosition,
                                       telehub.GroupRotation,
                                       spawnPoints);
            }
            else
            {
                client.SendTelehubInfo(UUID.Zero,
                                       String.Empty,
                                       Vector3.Zero,
                                       Quaternion.Identity,
                                       new List<Vector3>());
            }
        }
    }
}

