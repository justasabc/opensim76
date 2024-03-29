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
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Xml;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Mono.Addins;
using OpenSim.Services.Connectors.Hypergrid;

namespace OpenSim.Region.OptionalModules.Avatar.UserProfiles
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "UserProfilesModule")]
    public class UserProfileModule : IProfileModule, INonSharedRegionModule
    {
        /// <summary>
        /// Logging
        /// </summary>
        static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // The pair of Dictionaries are used to handle the switching of classified ads
        // by maintaining a cache of classified id to creator id mappings and an interest
        // count. The entries are removed when the interest count reaches 0.
        Dictionary<UUID, UUID> m_classifiedCache = new Dictionary<UUID, UUID>();
        Dictionary<UUID, int> m_classifiedInterest = new Dictionary<UUID, int>();

        public Scene Scene
        {
            get; private set;
        }

        /// <summary>
        /// Gets or sets the ConfigSource.
        /// </summary>
        /// <value>
        /// The configuration
        /// </value>
        public IConfigSource Config {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the URI to the profile server.
        /// </summary>
        /// <value>
        /// The profile server URI.
        /// </value>
        public string ProfileServerUri {
            get;
            set;
        }

        IProfileModule ProfileModule
        {
            get; set;
        }

        IUserManagement UserManagementModule
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets a value indicating whether this
        /// <see cref="OpenSim.Region.Coremodules.UserProfiles.UserProfileModule"/> is enabled.
        /// </summary>
        /// <value>
        /// <c>true</c> if enabled; otherwise, <c>false</c>.
        /// </value>
        public bool Enabled {
            get;
            set;
        }

        #region IRegionModuleBase implementation
        /// <summary>
        ///  This is called to initialize the region module. For shared modules, this is called exactly once, after
        /// creating the single (shared) instance. For non-shared modules, this is called once on each instance, after
        /// the instace for the region has been created. 
        /// </summary>
        /// <param name='source'>
        /// Source.
        /// </param>
        public void Initialise(IConfigSource source)
        {
            Config = source;
            ReplaceableInterface = typeof(IProfileModule);

            IConfig profileConfig = Config.Configs["UserProfiles"];

            if (profileConfig == null)
            {
                m_log.Debug("[PROFILES]: UserProfiles disabled, no configuration");
                Enabled = false;
                return;
            }

            // If we find ProfileURL then we configure for FULL support
            // else we setup for BASIC support
            ProfileServerUri = profileConfig.GetString("ProfileServiceURL", "");
            if (ProfileServerUri == "")
            {
                Enabled = false;
                return;
            }
                
            m_log.Debug("[PROFILES]: Full Profiles Enabled");
            ReplaceableInterface = null;
            Enabled = true;
        }

        /// <summary>
        /// Adds the region.
        /// </summary>
        /// <param name='scene'>
        /// Scene.
        /// </param>
        public void AddRegion(Scene scene)
        {
            if(!Enabled)
                return;

            Scene = scene;
            Scene.RegisterModuleInterface<IProfileModule>(this);
            Scene.EventManager.OnNewClient += OnNewClient;
            Scene.EventManager.OnMakeRootAgent += HandleOnMakeRootAgent;

            UserManagementModule = Scene.RequestModuleInterface<IUserManagement>();
        }

        void HandleOnMakeRootAgent (ScenePresence obj)
        {
            if(obj.PresenceType == PresenceType.Npc)
                return;

            Util.FireAndForget(delegate
            {
                GetImageAssets(((IScenePresence)obj).UUID);
            });
        }

        /// <summary>
        /// Removes the region.
        /// </summary>
        /// <param name='scene'>
        /// Scene.
        /// </param>
        public void RemoveRegion(Scene scene)
        {
            if(!Enabled)
                return;
        }

        /// <summary>
        ///  This will be called once for every scene loaded. In a shared module this will be multiple times in one
        /// instance, while a nonshared module instance will only be called once. This method is called after AddRegion
        /// has been called in all modules for that scene, providing an opportunity to request another module's
        /// interface, or hook an event from another module. 
        /// </summary>
        /// <param name='scene'>
        /// Scene.
        /// </param>
        public void RegionLoaded(Scene scene)
        {
            if(!Enabled)
                return;
        }

        /// <summary>
        ///  If this returns non-null, it is the type of an interface that this module intends to register. This will
        /// cause the loader to defer loading of this module until all other modules have been loaded. If no other
        /// module has registered the interface by then, this module will be activated, else it will remain inactive,
        /// letting the other module take over. This should return non-null ONLY in modules that are intended to be
        /// easily replaceable, e.g. stub implementations that the developer expects to be replaced by third party
        /// provided modules. 
        /// </summary>
        /// <value>
        /// The replaceable interface.
        /// </value>
        public Type ReplaceableInterface
        {
            get; private set;
        }

        /// <summary>
        /// Called as the instance is closed.
        /// </summary>
        public void Close()
        {
        }

        /// <value>
        ///  The name of the module 
        /// </value>
        /// <summary>
        /// Gets the module name.
        /// </summary>
        public string Name
        {
            get { return "UserProfileModule"; }
        }
        #endregion IRegionModuleBase implementation

        #region Region Event Handlers
        /// <summary>
        /// Raises the new client event.
        /// </summary>
        /// <param name='client'>
        /// Client.
        /// </param>
        void OnNewClient(IClientAPI client)
        {
            //Profile
            client.OnRequestAvatarProperties += RequestAvatarProperties;
            client.OnUpdateAvatarProperties += AvatarPropertiesUpdate;
            client.OnAvatarInterestUpdate += AvatarInterestsUpdate;

            // Classifieds
            client.AddGenericPacketHandler("avatarclassifiedsrequest", ClassifiedsRequest);
            client.OnClassifiedInfoUpdate += ClassifiedInfoUpdate;
            client.OnClassifiedInfoRequest += ClassifiedInfoRequest;
            client.OnClassifiedDelete += ClassifiedDelete;

            // Picks
            client.AddGenericPacketHandler("avatarpicksrequest", PicksRequest);
            client.AddGenericPacketHandler("pickinforequest", PickInfoRequest);
            client.OnPickInfoUpdate += PickInfoUpdate;
            client.OnPickDelete += PickDelete;

            // Notes
            client.AddGenericPacketHandler("avatarnotesrequest", NotesRequest);
            client.OnAvatarNotesUpdate += NotesUpdate;

            // Preferences
            client.OnUserInfoRequest += UserPreferencesRequest;
            client.OnUpdateUserInfo += UpdateUserPreferences;
        }
        #endregion Region Event Handlers

        #region Classified
        ///
        /// <summary>
        /// Handles the avatar classifieds request.
        /// </summary>
        /// <param name='sender'>
        /// Sender.
        /// </param>
        /// <param name='method'>
        /// Method.
        /// </param>
        /// <param name='args'>
        /// Arguments.
        /// </param>
        public void ClassifiedsRequest(Object sender, string method, List<String> args)
        {
            if (!(sender is IClientAPI))
                return;

            IClientAPI remoteClient = (IClientAPI)sender;

            UUID targetID;
            UUID.TryParse(args[0], out targetID);

            // Can't handle NPC yet...
            ScenePresence p = FindPresence(targetID);

            if (null != p)
            {
                if (p.PresenceType == PresenceType.Npc)
                    return;
            }

            string serverURI = string.Empty;
            GetUserProfileServerURI(targetID, out serverURI);
            UUID creatorId = UUID.Zero;

            OSDMap parameters= new OSDMap();
            UUID.TryParse(args[0], out creatorId);
            parameters.Add("creatorId", OSD.FromUUID(creatorId));
            OSD Params = (OSD)parameters;
            if(!JsonRpcRequest(ref Params, "avatarclassifiedsrequest", serverURI, UUID.Random().ToString()))
            {
                // Error Handling here!
                // if(parameters.ContainsKey("message")
            }

            parameters = (OSDMap)Params;

            OSDArray list = (OSDArray)parameters["result"];

            Dictionary<UUID, string> classifieds = new Dictionary<UUID, string>();

            foreach(OSD map in list)
            {
                OSDMap m = (OSDMap)map;
                UUID cid = m["classifieduuid"].AsUUID();
                string name = m["name"].AsString();

                classifieds[cid] = name;

                lock (m_classifiedCache)
                {
                    if (!m_classifiedCache.ContainsKey(cid))
                    {
                        m_classifiedCache.Add(cid,creatorId);
                        m_classifiedInterest.Add(cid, 0);
                    }

                    m_classifiedInterest[cid]++;
                }
            }

            remoteClient.SendAvatarClassifiedReply(new UUID(args[0]), classifieds);
        }

        public void ClassifiedInfoRequest(UUID queryClassifiedID, IClientAPI remoteClient)
        {    
            UUID target = remoteClient.AgentId;
            UserClassifiedAdd ad = new UserClassifiedAdd();
            ad.ClassifiedId = queryClassifiedID;

            lock (m_classifiedCache)
            {
                if (m_classifiedCache.ContainsKey(queryClassifiedID))
                {   
                    target = m_classifiedCache[queryClassifiedID];

                    m_classifiedInterest[queryClassifiedID] --;

                    if (m_classifiedInterest[queryClassifiedID] == 0)
                    {
                        m_classifiedInterest.Remove(queryClassifiedID);
                        m_classifiedCache.Remove(queryClassifiedID);
                    }
                }
            }
            
            string serverURI = string.Empty;
            GetUserProfileServerURI(target, out serverURI);

            object Ad = (object)ad;
            if(!JsonRpcRequest(ref Ad, "classifieds_info_query", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error getting classified info", false);
                return;
            }
            ad = (UserClassifiedAdd) Ad;

            if(ad.CreatorId == UUID.Zero)
                return;

            Vector3 globalPos = new Vector3();
            Vector3.TryParse(ad.GlobalPos, out globalPos);

            remoteClient.SendClassifiedInfoReply(ad.ClassifiedId, ad.CreatorId, (uint)ad.CreationDate, (uint)ad.ExpirationDate,
                                                 (uint)ad.Category, ad.Name, ad.Description, ad.ParcelId, (uint)ad.ParentEstate,
                                                 ad.SnapshotId, ad.SimName, globalPos, ad.ParcelName, ad.Flags, ad.Price);

        }

        /// <summary>
        /// Classifieds info update.
        /// </summary>
        /// <param name='queryclassifiedID'>
        /// Queryclassified I.
        /// </param>
        /// <param name='queryCategory'>
        /// Query category.
        /// </param>
        /// <param name='queryName'>
        /// Query name.
        /// </param>
        /// <param name='queryDescription'>
        /// Query description.
        /// </param>
        /// <param name='queryParcelID'>
        /// Query parcel I.
        /// </param>
        /// <param name='queryParentEstate'>
        /// Query parent estate.
        /// </param>
        /// <param name='querySnapshotID'>
        /// Query snapshot I.
        /// </param>
        /// <param name='queryGlobalPos'>
        /// Query global position.
        /// </param>
        /// <param name='queryclassifiedFlags'>
        /// Queryclassified flags.
        /// </param>
        /// <param name='queryclassifiedPrice'>
        /// Queryclassified price.
        /// </param>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        public void ClassifiedInfoUpdate(UUID queryclassifiedID, uint queryCategory, string queryName, string queryDescription, UUID queryParcelID,
                                         uint queryParentEstate, UUID querySnapshotID, Vector3 queryGlobalPos, byte queryclassifiedFlags,
                                         int queryclassifiedPrice, IClientAPI remoteClient)
        {
            UserClassifiedAdd ad = new UserClassifiedAdd();

            Scene s = (Scene) remoteClient.Scene;
            Vector3 pos = remoteClient.SceneAgent.AbsolutePosition;
            ILandObject land = s.LandChannel.GetLandObject(pos.X, pos.Y);
            ScenePresence p = FindPresence(remoteClient.AgentId);
//            Vector3 avaPos = p.AbsolutePosition;
            
            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

            if (land == null)
            {
                ad.ParcelName = string.Empty;
            }
            else
            {
                ad.ParcelName = land.LandData.Name;
            }

            ad.CreatorId = remoteClient.AgentId;
            ad.ClassifiedId = queryclassifiedID;
            ad.Category = Convert.ToInt32(queryCategory);
            ad.Name = queryName;
            ad.Description = queryDescription;
            ad.ParentEstate = Convert.ToInt32(queryParentEstate);
            ad.SnapshotId = querySnapshotID;
            ad.SimName = remoteClient.Scene.RegionInfo.RegionName;
            ad.GlobalPos = queryGlobalPos.ToString ();
            ad.Flags = queryclassifiedFlags;
            ad.Price = queryclassifiedPrice;
            ad.ParcelId = p.currentParcelUUID;

            object Ad = ad;

            OSD.SerializeMembers(Ad);

            if(!JsonRpcRequest(ref Ad, "classified_update", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error updating classified", false);
            }
        }

        /// <summary>
        /// Classifieds delete.
        /// </summary>
        /// <param name='queryClassifiedID'>
        /// Query classified I.
        /// </param>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        public void ClassifiedDelete(UUID queryClassifiedID, IClientAPI remoteClient)
        {
            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

            UUID classifiedId;
            OSDMap parameters= new OSDMap();
            UUID.TryParse(queryClassifiedID.ToString(), out classifiedId);
            parameters.Add("classifiedId", OSD.FromUUID(classifiedId));
            OSD Params = (OSD)parameters;
            if(!JsonRpcRequest(ref Params, "classified_delete", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error classified delete", false);
            }

            parameters = (OSDMap)Params;
        }
        #endregion Classified

        #region Picks
        /// <summary>
        /// Handles the avatar picks request.
        /// </summary>
        /// <param name='sender'>
        /// Sender.
        /// </param>
        /// <param name='method'>
        /// Method.
        /// </param>
        /// <param name='args'>
        /// Arguments.
        /// </param>
        public void PicksRequest(Object sender, string method, List<String> args)
        {
            if (!(sender is IClientAPI))
                return;

            IClientAPI remoteClient = (IClientAPI)sender;

            UUID targetId;
            UUID.TryParse(args[0], out targetId);

            // Can't handle NPC yet...
            ScenePresence p = FindPresence(targetId);

            if (null != p)
            {
                if (p.PresenceType == PresenceType.Npc)
                    return;
            }

            string serverURI = string.Empty;
            GetUserProfileServerURI(targetId, out serverURI);

            OSDMap parameters= new OSDMap();
            parameters.Add("creatorId", OSD.FromUUID(targetId));
            OSD Params = (OSD)parameters;
            if(!JsonRpcRequest(ref Params, "avatarpicksrequest", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error requesting picks", false);
                return;
            }

            parameters = (OSDMap)Params;

            OSDArray list = (OSDArray)parameters["result"];

            Dictionary<UUID, string> picks = new Dictionary<UUID, string>();

            foreach(OSD map in list)
            {
                OSDMap m = (OSDMap)map;
                UUID cid = m["pickuuid"].AsUUID();
                string name = m["name"].AsString();
                
                m_log.DebugFormat("[PROFILES]: PicksRequest {0}", name);

                picks[cid] = name;
            }
            remoteClient.SendAvatarPicksReply(new UUID(args[0]), picks);
        }

        /// <summary>
        /// Handles the pick info request.
        /// </summary>
        /// <param name='sender'>
        /// Sender.
        /// </param>
        /// <param name='method'>
        /// Method.
        /// </param>
        /// <param name='args'>
        /// Arguments.
        /// </param>
        public void PickInfoRequest(Object sender, string method, List<String> args)
        {
            if (!(sender is IClientAPI))
                return;

            UUID targetID;
            UUID.TryParse(args[0], out targetID);
            string serverURI = string.Empty;
            GetUserProfileServerURI(targetID, out serverURI);
            IClientAPI remoteClient = (IClientAPI)sender;

            UserProfilePick pick = new UserProfilePick();
            UUID.TryParse(args[0], out pick.CreatorId);
            UUID.TryParse(args[1], out pick.PickId);

                
            object Pick = (object)pick;
            if(!JsonRpcRequest(ref Pick, "pickinforequest", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error selecting pick", false);
            }
            pick = (UserProfilePick) Pick;
            if(pick.SnapshotId == UUID.Zero)
            {
                // In case of a new UserPick, the data may not be ready and we would send wrong data, skip it...
                m_log.DebugFormat("[PROFILES]: PickInfoRequest: SnapshotID is {0}", UUID.Zero.ToString());
                return;
            }
             
            Vector3 globalPos;
            Vector3.TryParse(pick.GlobalPos,out globalPos);

            m_log.DebugFormat("[PROFILES]: PickInfoRequest: {0} : {1}", pick.Name.ToString(), pick.SnapshotId.ToString());

            remoteClient.SendPickInfoReply(pick.PickId,pick.CreatorId,pick.TopPick,pick.ParcelId,pick.Name,
                                           pick.Desc,pick.SnapshotId,pick.User,pick.OriginalName,pick.SimName,
                                           globalPos,pick.SortOrder,pick.Enabled);
        }

        /// <summary>
        /// Updates the userpicks
        /// </summary>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        /// <param name='pickID'>
        /// Pick I.
        /// </param>
        /// <param name='creatorID'>
        /// the creator of the pick
        /// </param>
        /// <param name='topPick'>
        /// Top pick.
        /// </param>
        /// <param name='name'>
        /// Name.
        /// </param>
        /// <param name='desc'>
        /// Desc.
        /// </param>
        /// <param name='snapshotID'>
        /// Snapshot I.
        /// </param>
        /// <param name='sortOrder'>
        /// Sort order.
        /// </param>
        /// <param name='enabled'>
        /// Enabled.
        /// </param>
        public void PickInfoUpdate(IClientAPI remoteClient, UUID pickID, UUID creatorID, bool topPick, string name, string desc, UUID snapshotID, int sortOrder, bool enabled)
        {
            
            m_log.DebugFormat("[PROFILES]: Start PickInfoUpdate Name: {0} PickId: {1} SnapshotId: {2}", name, pickID.ToString(), snapshotID.ToString());
            UserProfilePick pick = new UserProfilePick();
            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);
            ScenePresence p = FindPresence(remoteClient.AgentId);

            Vector3 avaPos = p.AbsolutePosition;
            // Getting the global position for the Avatar
            Vector3 posGlobal = new Vector3(remoteClient.Scene.RegionInfo.RegionLocX*Constants.RegionSize + avaPos.X,
                                            remoteClient.Scene.RegionInfo.RegionLocY*Constants.RegionSize + avaPos.Y,
                                            avaPos.Z);

            string landOwnerName = string.Empty;
            ILandObject land =  p.Scene.LandChannel.GetLandObject(avaPos.X, avaPos.Y);
            if(land.LandData.IsGroupOwned)
            {
                IGroupsModule groupMod = p.Scene.RequestModuleInterface<IGroupsModule>();
                UUID groupId = land.LandData.GroupID;
                GroupRecord groupRecord = groupMod.GetGroupRecord(groupId);
                landOwnerName = groupRecord.GroupName;
            }
            else
            {
                IUserAccountService accounts = p.Scene.RequestModuleInterface<IUserAccountService>();
                UserAccount user = accounts.GetUserAccount(p.Scene.RegionInfo.ScopeID, land.LandData.OwnerID);
                landOwnerName = user.Name;
            }

            pick.PickId = pickID;
            pick.CreatorId = creatorID;
            pick.TopPick = topPick;
            pick.Name = name;
            pick.Desc = desc;
            pick.ParcelId = p.currentParcelUUID;
            pick.SnapshotId = snapshotID;
            pick.User = landOwnerName;
            pick.SimName = remoteClient.Scene.RegionInfo.RegionName;
            pick.GlobalPos = posGlobal.ToString();
            pick.SortOrder = sortOrder;
            pick.Enabled = enabled;

            object Pick = (object)pick;
            if(!JsonRpcRequest(ref Pick, "picks_update", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error updating pick", false);
            }

            m_log.DebugFormat("[PROFILES]: Finish PickInfoUpdate {0} {1}", pick.Name, pick.PickId.ToString());
        }

        /// <summary>
        /// Delete a Pick
        /// </summary>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        /// <param name='queryPickID'>
        /// Query pick I.
        /// </param>
        public void PickDelete(IClientAPI remoteClient, UUID queryPickID)
        {
            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

            OSDMap parameters= new OSDMap();
            parameters.Add("pickId", OSD.FromUUID(queryPickID));
            OSD Params = (OSD)parameters;
            if(!JsonRpcRequest(ref Params, "picks_delete", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error picks delete", false);
            }
        }
        #endregion Picks

        #region Notes
        /// <summary>
        /// Handles the avatar notes request.
        /// </summary>
        /// <param name='sender'>
        /// Sender.
        /// </param>
        /// <param name='method'>
        /// Method.
        /// </param>
        /// <param name='args'>
        /// Arguments.
        /// </param>
        public void NotesRequest(Object sender, string method, List<String> args)
        {
            UserProfileNotes note = new UserProfileNotes();

            if (!(sender is IClientAPI))
                return;

            IClientAPI remoteClient = (IClientAPI)sender;
            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);
            note.UserId = remoteClient.AgentId;
            UUID.TryParse(args[0], out note.TargetId);

            object Note = (object)note;
            if(!JsonRpcRequest(ref Note, "avatarnotesrequest", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error requesting note", false);
            }
            note = (UserProfileNotes) Note;
                
            remoteClient.SendAvatarNotesReply(note.TargetId, note.Notes);
        }

        /// <summary>
        /// Avatars the notes update.
        /// </summary>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        /// <param name='queryTargetID'>
        /// Query target I.
        /// </param>
        /// <param name='queryNotes'>
        /// Query notes.
        /// </param>
        public void NotesUpdate(IClientAPI remoteClient, UUID queryTargetID, string queryNotes)
        {
            UserProfileNotes note = new UserProfileNotes();

            note.UserId = remoteClient.AgentId;
            note.TargetId = queryTargetID;
            note.Notes = queryNotes;

            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

            object Note = note;
            if(!JsonRpcRequest(ref Note, "avatar_notes_update", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error updating note", false);
            }
        }
        #endregion Notes

        #region User Preferences
        /// <summary>
        /// Updates the user preferences.
        /// </summary>
        /// <param name='imViaEmail'>
        /// Im via email.
        /// </param>
        /// <param name='visible'>
        /// Visible.
        /// </param>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        public void UpdateUserPreferences(bool imViaEmail, bool visible, IClientAPI remoteClient)
        {
            UserPreferences pref = new UserPreferences();

            pref.UserId = remoteClient.AgentId;
            pref.IMViaEmail = imViaEmail;
            pref.Visible = visible;

            string serverURI = string.Empty;
            bool foreign = GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

            object Pref = pref;
            if(!JsonRpcRequest(ref Pref, "user_preferences_update", serverURI, UUID.Random().ToString()))
            {
                m_log.InfoFormat("[PROFILES]: UserPreferences update error");
                remoteClient.SendAgentAlertMessage("Error updating preferences", false);
                return;   
            }
        }
        
        /// <summary>
        /// Users the preferences request.
        /// </summary>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        public void UserPreferencesRequest(IClientAPI remoteClient)
        {
            UserPreferences pref = new UserPreferences();

            pref.UserId = remoteClient.AgentId;

            string serverURI = string.Empty;
            bool foreign = GetUserProfileServerURI(remoteClient.AgentId, out serverURI);


            object Pref = (object)pref;
            if(!JsonRpcRequest(ref Pref, "user_preferences_request", serverURI, UUID.Random().ToString()))
            {
                m_log.InfoFormat("[PROFILES]: UserPreferences request error");
                remoteClient.SendAgentAlertMessage("Error requesting preferences", false);
                return;
            }
            pref = (UserPreferences) Pref;

            remoteClient.SendUserInfoReply(pref.IMViaEmail, pref.Visible, pref.EMail);
       
        }
        #endregion User Preferences

        #region Avatar Properties
        /// <summary>
        /// Update the avatars interests .
        /// </summary>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        /// <param name='wantmask'>
        /// Wantmask.
        /// </param>
        /// <param name='wanttext'>
        /// Wanttext.
        /// </param>
        /// <param name='skillsmask'>
        /// Skillsmask.
        /// </param>
        /// <param name='skillstext'>
        /// Skillstext.
        /// </param>
        /// <param name='languages'>
        /// Languages.
        /// </param>
        public void AvatarInterestsUpdate(IClientAPI remoteClient, uint wantmask, string wanttext, uint skillsmask, string skillstext, string languages)
        {
            UserProfileProperties prop = new UserProfileProperties();

            prop.UserId = remoteClient.AgentId;
            prop.WantToMask = (int)wantmask;
            prop.WantToText = wanttext;
            prop.SkillsMask = (int)skillsmask;
            prop.SkillsText = skillstext;
            prop.Language = languages;

            string serverURI = string.Empty;
            GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

            object Param = prop;
            if(!JsonRpcRequest(ref Param, "avatar_interests_update", serverURI, UUID.Random().ToString()))
            {
                remoteClient.SendAgentAlertMessage(
                        "Error updating interests", false);
            }
        }

        public void RequestAvatarProperties(IClientAPI remoteClient, UUID avatarID)
        {
            if ( String.IsNullOrEmpty(avatarID.ToString()) || String.IsNullOrEmpty(remoteClient.AgentId.ToString()))
            {
                // Looking for a reason that some viewers are sending null Id's
                m_log.DebugFormat("[PROFILES]: This should not happen remoteClient.AgentId {0} - avatarID {1}", remoteClient.AgentId, avatarID);
                return;
            }

            // Can't handle NPC yet...
            ScenePresence p = FindPresence(avatarID);

            if (null != p)
            {
                if (p.PresenceType == PresenceType.Npc)
                    return;
            }

            string serverURI = string.Empty;
            bool foreign = GetUserProfileServerURI(avatarID, out serverURI);

            UserAccount account = null;
            Dictionary<string,object> userInfo;

            if (!foreign)
            {
                account = Scene.UserAccountService.GetUserAccount(Scene.RegionInfo.ScopeID, avatarID);
            }
            else
            {
                userInfo = new Dictionary<string, object>();
            }

            Byte[] charterMember = new Byte[1];
            string born = String.Empty;
            uint flags = 0x00;

            if (null != account)
            {
                if (account.UserTitle == "")
                {
                    charterMember[0] = (Byte)((account.UserFlags & 0xf00) >> 8);
                }
                else
                {
                    charterMember = Utils.StringToBytes(account.UserTitle);
                }

                born = Util.ToDateTime(account.Created).ToString(
                                  "M/d/yyyy", CultureInfo.InvariantCulture);
                flags = (uint)(account.UserFlags & 0xff);
            }
            else
            {
                if (GetUserAccountData(avatarID, out userInfo) == true)
                {
                    if ((string)userInfo["user_title"] == "")
                    {
                        charterMember[0] = (Byte)(((Byte)userInfo["user_flags"] & 0xf00) >> 8);
                    }
                    else
                    {
                        charterMember = Utils.StringToBytes((string)userInfo["user_title"]);
                    }

                    int val_born = (int)userInfo["user_created"];
                    born = Util.ToDateTime(val_born).ToString(
                                  "M/d/yyyy", CultureInfo.InvariantCulture);

                    // picky, picky
                    int val_flags = (int)userInfo["user_flags"];
                    flags = (uint)(val_flags & 0xff);
                }
            }

            UserProfileProperties props = new UserProfileProperties();
            string result = string.Empty;

            props.UserId = avatarID;
            GetProfileData(ref props, out result);

            remoteClient.SendAvatarProperties(props.UserId, props.AboutText, born, charterMember , props.FirstLifeText, flags,
                                              props.FirstLifeImageId, props.ImageId, props.WebUrl, props.PartnerId);


            remoteClient.SendAvatarInterestsReply(props.UserId, (uint)props.WantToMask, props.WantToText, (uint)props.SkillsMask,
                                                  props.SkillsText, props.Language);
        }

        /// <summary>
        /// Updates the avatar properties.
        /// </summary>
        /// <param name='remoteClient'>
        /// Remote client.
        /// </param>
        /// <param name='newProfile'>
        /// New profile.
        /// </param>
        public void AvatarPropertiesUpdate(IClientAPI remoteClient, UserProfileData newProfile)
        {
            if (remoteClient.AgentId == newProfile.ID)
            {
                UserProfileProperties prop = new UserProfileProperties();

                prop.UserId = remoteClient.AgentId;
                prop.WebUrl = newProfile.ProfileUrl;
                prop.ImageId = newProfile.Image;
                prop.AboutText = newProfile.AboutText;
                prop.FirstLifeImageId = newProfile.FirstLifeImage;
                prop.FirstLifeText = newProfile.FirstLifeAboutText;

                string serverURI = string.Empty;
                GetUserProfileServerURI(remoteClient.AgentId, out serverURI);

                object Prop = prop;

                if(!JsonRpcRequest(ref Prop, "avatar_properties_update", serverURI, UUID.Random().ToString()))
                {
                    remoteClient.SendAgentAlertMessage(
                            "Error updating properties", false);
                }

                RequestAvatarProperties(remoteClient, newProfile.ID);
            }
        }

        /// <summary>
        /// Gets the profile data.
        /// </summary>
        /// <returns>
        /// The profile data.
        /// </returns>
        /// <param name='userID'>
        /// User I.
        /// </param>
        bool GetProfileData(ref UserProfileProperties properties, out string message)
        {
            // Can't handle NPC yet...
            ScenePresence p = FindPresence(properties.UserId);

            if (null != p)
            {
                if (p.PresenceType == PresenceType.Npc)
                {
                    message = "Id points to NPC";
                    return false;
                }
            }

            string serverURI = string.Empty;
            GetUserProfileServerURI(properties.UserId, out serverURI);

            // This is checking a friend on the home grid
            // Not HG friend
            if ( String.IsNullOrEmpty(serverURI))
            {
                message = "No Presence - foreign friend";
                return false;
            }

            object Prop = (object)properties;
            JsonRpcRequest(ref Prop, "avatar_properties_request", serverURI, UUID.Random().ToString());
            properties = (UserProfileProperties)Prop;

            message = "Success";
            return true;
        }
        #endregion Avatar Properties

        #region Utils
        bool GetImageAssets(UUID avatarId)
        {
            string profileServerURI = string.Empty;
            string assetServerURI = string.Empty;

            bool foreign = GetUserProfileServerURI(avatarId, out profileServerURI);

            if(!foreign)
                return true;

            assetServerURI = UserManagementModule.GetUserServerURL(avatarId, "AssetServerURI");

            OSDMap parameters= new OSDMap();
            parameters.Add("avatarId", OSD.FromUUID(avatarId));
            OSD Params = (OSD)parameters;
            if(!JsonRpcRequest(ref Params, "image_assets_request", profileServerURI, UUID.Random().ToString()))
            {
                // Error Handling here!
                // if(parameters.ContainsKey("message")
                return false;
            }
            
            parameters = (OSDMap)Params;
            
            OSDArray list = (OSDArray)parameters["result"];
            
            foreach(OSD asset in list)
            {
                OSDString assetId = (OSDString)asset;

                Scene.AssetService.Get(string.Format("{0}/{1}",assetServerURI, assetId.AsString()));
            }
            return true;
        }

        /// <summary>
        /// Gets the user account data.
        /// </summary>
        /// <returns>
        /// The user profile data.
        /// </returns>
        /// <param name='userID'>
        /// If set to <c>true</c> user I.
        /// </param>
        /// <param name='userInfo'>
        /// If set to <c>true</c> user info.
        /// </param>
        bool GetUserAccountData(UUID userID, out Dictionary<string, object> userInfo)
        {
            Dictionary<string,object> info = new Dictionary<string, object>();

            if (UserManagementModule.IsLocalGridUser(userID))
            {
                // Is local
                IUserAccountService uas = Scene.UserAccountService;
                UserAccount account = uas.GetUserAccount(Scene.RegionInfo.ScopeID, userID);

                info["user_flags"] = account.UserFlags;
                info["user_created"] = account.Created;

                if (!String.IsNullOrEmpty(account.UserTitle))
                    info["user_title"] = account.UserTitle;
                else
                    info["user_title"] = "";

                userInfo = info;

                return false;
            }
            else
            {
                // Is Foreign
                string home_url = UserManagementModule.GetUserServerURL(userID, "HomeURI");

                if (String.IsNullOrEmpty(home_url))
                {
                    info["user_flags"] = 0;
                    info["user_created"] = 0;
                    info["user_title"] = "Unavailable";

                    userInfo = info;
                    return true;
                }

                UserAgentServiceConnector uConn = new UserAgentServiceConnector(home_url);

                Dictionary<string, object> account = uConn.GetUserInfo(userID);

                if (account.Count > 0)
                {
                    if (account.ContainsKey("user_flags"))
                        info["user_flags"] = account["user_flags"];
                    else
                        info["user_flags"] = "";

                    if (account.ContainsKey("user_created"))
                        info["user_created"] = account["user_created"];
                    else
                        info["user_created"] = "";

                    info["user_title"] = "HG Visitor";
                }
                else
                {
                   info["user_flags"] = 0;
                   info["user_created"] = 0;
                   info["user_title"] = "HG Visitor";
                }
                userInfo = info;
                return true;
            }
        }

        /// <summary>
        /// Gets the user profile server UR.
        /// </summary>
        /// <returns>
        /// The user profile server UR.
        /// </returns>
        /// <param name='userID'>
        /// If set to <c>true</c> user I.
        /// </param>
        /// <param name='serverURI'>
        /// If set to <c>true</c> server UR.
        /// </param>
        bool GetUserProfileServerURI(UUID userID, out string serverURI)
        {
            bool local;
            local = UserManagementModule.IsLocalGridUser(userID);

            if (!local)
            {
                serverURI = UserManagementModule.GetUserServerURL(userID, "ProfileServerURI");
                // Is Foreign
                return true;
            }
            else
            {
                serverURI = ProfileServerUri;
                // Is local
                return false;
            }
        }

        /// <summary>
        /// Finds the presence.
        /// </summary>
        /// <returns>
        /// The presence.
        /// </returns>
        /// <param name='clientID'>
        /// Client I.
        /// </param>
        ScenePresence FindPresence(UUID clientID)
        {
            ScenePresence p;

            p = Scene.GetScenePresence(clientID);
            if (p != null && !p.IsChildAgent)
                return p;

            return null;
        }
        #endregion Util

        #region Web Util
        /// <summary>
        /// Sends json-rpc request with a serializable type.
        /// </summary>
        /// <returns>
        /// OSD Map.
        /// </returns>
        /// <param name='parameters'>
        /// Serializable type .
        /// </param>
        /// <param name='method'>
        /// Json-rpc method to call.
        /// </param>
        /// <param name='uri'>
        /// URI of json-rpc service.
        /// </param>
        /// <param name='jsonId'>
        /// Id for our call.
        /// </param>
        bool JsonRpcRequest(ref object parameters, string method, string uri, string jsonId)
        {
            if (jsonId == null)
                throw new ArgumentNullException ("jsonId");
            if (uri == null)
                throw new ArgumentNullException ("uri");
            if (method == null)
                throw new ArgumentNullException ("method");
            if (parameters == null)
                throw new ArgumentNullException ("parameters");

            // Prep our payload
            OSDMap json = new OSDMap();

            json.Add("jsonrpc", OSD.FromString("2.0"));
            json.Add("id", OSD.FromString(jsonId));
            json.Add("method", OSD.FromString(method));
            // Experiment
            json.Add("params", OSD.SerializeMembers(parameters));

            string jsonRequestData = OSDParser.SerializeJsonString(json);
            byte[] content = Encoding.UTF8.GetBytes(jsonRequestData);

            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(uri);
            // webRequest.Credentials = new NetworkCredential(rpcUser, rpcPass);
            webRequest.ContentType = "application/json-rpc";
            webRequest.Method = "POST";

            Stream dataStream = webRequest.GetRequestStream();
            dataStream.Write(content, 0, content.Length);
            dataStream.Close();

            WebResponse webResponse = null;
            try
            {
                webResponse = webRequest.GetResponse();
            }
            catch (WebException e)
            {
                Console.WriteLine("Web Error" + e.Message);
                Console.WriteLine ("Please check input");
                return false;
            }

            Stream rstream = webResponse.GetResponseStream();
            OSDMap mret = (OSDMap)OSDParser.DeserializeJson(rstream);

            if (mret.ContainsKey("error"))
                return false;
            
            // get params...
            OSD.DeserializeMembers(ref parameters, (OSDMap) mret["result"]);
            return true;
        }

        /// <summary>
        /// Sends json-rpc request with OSD parameter.
        /// </summary>
        /// <returns>
        /// The rpc request.
        /// </returns>
        /// <param name='data'>
        /// data - incoming as parameters, outgong as result/error
        /// </param>
        /// <param name='method'>
        /// Json-rpc method to call.
        /// </param>
        /// <param name='uri'>
        /// URI of json-rpc service.
        /// </param>
        /// <param name='jsonId'>
        /// If set to <c>true</c> json identifier.
        /// </param>
        bool JsonRpcRequest(ref OSD data, string method, string uri, string jsonId)
        {
            OSDMap map = new OSDMap();

            map["jsonrpc"] = "2.0";
            if(string.IsNullOrEmpty(jsonId))
                map["id"] = UUID.Random().ToString();
            else
                map["id"] = jsonId;

            map["method"] = method;
            map["params"] = data;

            string jsonRequestData = OSDParser.SerializeJsonString(map);
            byte[] content = Encoding.UTF8.GetBytes(jsonRequestData);

            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(uri);
            webRequest.ContentType = "application/json-rpc";
            webRequest.Method = "POST";

            Stream dataStream = webRequest.GetRequestStream();
            dataStream.Write(content, 0, content.Length);
            dataStream.Close();

            WebResponse webResponse = null;
            try
            {
                webResponse = webRequest.GetResponse();
            }
            catch (WebException e)
            {
                Console.WriteLine("Web Error" + e.Message);
                Console.WriteLine ("Please check input");
                return false;
            }

            Stream rstream = webResponse.GetResponseStream();

            OSDMap response = new OSDMap();
            try
            {
                response = (OSDMap)OSDParser.DeserializeJson(rstream);
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[PROFILES]: JsonRpcRequest Error {0} - remote user with legacy profiles?", e.Message);
                return false;
            }

            if(response.ContainsKey("error"))
            {
                data = response["error"];
                return false;
            }

            data = response;

            return true;
        }
        #endregion Web Util
    }
}