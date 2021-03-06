using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using ACE.Database.Models.Shard;

using aclogview.ACE_Helpers;

namespace aclogview.Tools.Scrapers
{
    class PlayerExporter : Scraper
    {
        public override string Description => "Exports players and their inventory";

        // TODO: player.Character.CharacterPropertiesQuestRegistry
        // TODO: player.Character.HairTexture
        // TODO: player.Character.DefaultHairTexture

        class WorldObjectItem
        {
            public readonly Biota Biota = new Biota();
            public readonly string Name;

            public bool AppraiseInfoReceived;

            public WorldObjectItem(uint guid, string name)
            {
                Biota.Id = guid;
                Name = name;
            }
        }

        class LoginEvent
        {
            public readonly string FileName;
            public readonly uint TSec;

            public readonly Biota Biota = new Biota();
            public readonly Character Character = new Character();

            public readonly List<(uint guid, uint containerProperties)> Inventory = new List<(uint guid, uint containerProperties)>();
            public readonly List<(uint guid, uint location, uint priority)> Equipment = new List<(uint guid, uint location, uint priority)>();

            public readonly Dictionary<uint, List<uint>> ViewContentsEvents = new Dictionary<uint, List<uint>>();
            public bool PlayerLoginCompleted;

            public readonly Dictionary<uint, WorldObjectItem> WorldObjects = new Dictionary<uint, WorldObjectItem>();

            public LoginEvent(string fileName, uint tsec, uint guid)
            {
                FileName = fileName;
                TSec = tsec;

                Biota.Id = guid;
                Character.Id = guid;
            }

            public bool IsPossedItem(uint guid)
            {
                foreach (var entry in Inventory)
                {
                    if (entry.guid == guid)
                        return true;

                    if (ViewContentsEvents.TryGetValue(entry.guid, out var value))
                    {
                        if (value.Contains(guid))
                            return true;
                    }
                }

                foreach (var entry in Equipment)
                {
                    if (entry.guid == guid)
                        return true;
                }

                return false;
            }
        }

        class Player
        {
            public readonly List<LoginEvent> LoginEvents = new List<LoginEvent>();

            /// <summary>
            /// Searches all possessions to find the last one recorded that also has AppraiseInfoReceived
            /// </summary>
            public WorldObjectItem GetBestPossession(uint guid, string name = null)
            {
                WorldObjectItem best = null;

                var sortedLoginEvents = LoginEvents.OrderBy(r => r.TSec).ToList();

                foreach (var loginEvent in sortedLoginEvents)
                {
                    if (loginEvent.WorldObjects.TryGetValue(guid, out var woi))
                    {
                        if (woi.Name != name)
                            continue;

                        if (best == null || woi.AppraiseInfoReceived)
                            best = woi;
                    }
                }

                return best;
            }
        }

        private readonly Dictionary<string, Dictionary<uint, Player>> playersByServer = new Dictionary<string, Dictionary<uint, Player>>();

        public override void Reset()
        {
            playersByServer.Clear();
        }

        public override (int hits, int messageExceptions) ProcessFileRecords(string fileName, List<PacketRecord> records, ref bool searchAborted)
        {
            int hits = 0;
            int messageExceptions = 0;

            string serverName = "Unknown";

            LoginEvent loginEvent = null;

            var rwLock = new ReaderWriterLockSlim();

            // Determine the server name using the Server List.
            // This will be overriden if a Evt_Login__WorldInfo_ID is received
            if (records.Count > 0)
            {
                var servers = ServerList.FindBy(records[0].ipHeader, records[0].isSend);

                if (servers.Count == 1 && servers[0].IsRetail)
                    serverName = servers[0].Name;
            }

            foreach (PacketRecord record in records)
            {
                if (searchAborted)
                    return (hits, messageExceptions);

                try
                {
                    if (record.data.Length <= 4)
                        continue;

                    using (var memoryStream = new MemoryStream(record.data))
                    using (var binaryReader = new BinaryReader(memoryStream))
                    {
                        var messageCode = binaryReader.ReadUInt32();

                        if (messageCode == (uint)PacketOpcode.Evt_Login__WorldInfo_ID) // 0xF7E1
                        {
                            var message = CM_Login.WorldInfo.read(binaryReader);
                            serverName = message.strWorldName.m_buffer;
                            continue;
                        }

                        // This could be seen multiple times if the first time the player tries to enter, they get a "Your character is already in world" message
                        if (messageCode == (uint)PacketOpcode.CHARACTER_ENTER_GAME_EVENT) // 0xF657
                        {
                            var message = Proto_UI.EnterWorld.read(binaryReader);

                            loginEvent = new LoginEvent(fileName, record.tsSec, message.gid);
                            loginEvent.Biota.SetProperty(ACE.Entity.Enum.Properties.PropertyString.PCAPRecordedServerName, serverName, rwLock, out _);
                            continue;
                        }

                        if (loginEvent == null)
                            continue;

                        if (messageCode == (uint)PacketOpcode.CHARACTER_EXIT_GAME_EVENT)
                        {
                            loginEvent = null;
                            continue;
                        }

                        if (messageCode == (uint)PacketOpcode.ORDERED_EVENT) // 0xF7B1 Game Action
                        {
                            /*var sequence = */
                            binaryReader.ReadUInt32();
                            var opCode = binaryReader.ReadUInt32();

                            if (opCode == (uint)PacketOpcode.Evt_Character__LoginCompleteNotification_ID)
                            {
                                // At this point, we should stop building/updating the player/character and only update the possessed items
                                loginEvent.PlayerLoginCompleted = true;
                            }
                        }

                        if (messageCode == (uint)PacketOpcode.WEENIE_ORDERED_EVENT) // 0xF7B0 Game Event
                        {
                            /*var guid = */
                            binaryReader.ReadUInt32();
                            /*var sequence = */
                            binaryReader.ReadUInt32();
                            var opCode = binaryReader.ReadUInt32();

                            // We only process player create/update messages for player biotas during the login process
                            if (!loginEvent.PlayerLoginCompleted)
                            {
                                if (opCode == (uint)PacketOpcode.PLAYER_DESCRIPTION_EVENT)
                                {
                                    hits++;

                                    var message = CM_Login.PlayerDescription.read(binaryReader);

                                    ACEBiotaCreator.Update(message, loginEvent.Character, loginEvent.Biota, loginEvent.Inventory, loginEvent.Equipment, rwLock);

                                    if (!playersByServer.TryGetValue(serverName, out var server))
                                    {
                                        server = new Dictionary<uint, Player>();
                                        playersByServer[serverName] = server;
                                    }

                                    if (!server.TryGetValue(loginEvent.Biota.Id, out var player))
                                    {
                                        player = new Player();
                                        server[loginEvent.Biota.Id] = player;
                                    }

                                    player.LoginEvents.Add(loginEvent);

                                }
                                else if (opCode == (uint)PacketOpcode.Evt_Social__FriendsUpdate_ID)
                                {
                                    // Skip this
                                    // player.Character.CharacterPropertiesFriendList
                                }
                                else if (opCode == (uint)PacketOpcode.Evt_Social__CharacterTitleTable_ID)
                                {
                                    var message = CM_Social.CharacterTitleTable.read(binaryReader);

                                    loginEvent.Biota.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.CharacterTitleId, (int)message.mDisplayTitle, rwLock, out _);

                                    foreach (var value in message.mTitleList.list)
                                        loginEvent.Character.CharacterPropertiesTitleBook.Add(new CharacterPropertiesTitleBook { TitleId = (uint)value });
                                }
                                else if (opCode == (uint)PacketOpcode.Evt_Social__SendClientContractTrackerTable_ID)
                                {
                                    var message = CM_Social.SendClientContractTrackerTable.read(binaryReader);

                                    foreach (var value in message._contractTrackerHash.hashTable)
                                    {
                                        var contract = new CharacterPropertiesContract
                                        {
                                            // value.Key what's this?
                                            ContractId = value.Value._contract_id,
                                            Stage = value.Value._contract_stage,
                                            TimeWhenDone = (ulong)value.Value._time_when_done,
                                            TimeWhenRepeats = (ulong)value.Value._time_when_repeats,
                                            Version = value.Value._version,
                                        };
                                        loginEvent.Character.CharacterPropertiesContract.Add(contract);
                                    }
                                }
                                else if (opCode == (uint)PacketOpcode.ALLEGIANCE_UPDATE_EVENT)
                                {
                                    // Skip this
                                }
                                else if (opCode == (uint)PacketOpcode.VIEW_CONTENTS_EVENT)
                                {
                                    var message = CM_Inventory.ViewContents.read(binaryReader);

                                    var list = new List<uint>();

                                    foreach (var value in message.contents_list.list)
                                        list.Add(value.m_iid); // We don't use m_uContainerProperties

                                    if (!loginEvent.ViewContentsEvents.ContainsKey(message.i_container)) // We only store the first ViewContentsEvent
                                        loginEvent.ViewContentsEvents[message.i_container] = list;
                                }
                            }

                            if (opCode == (uint)PacketOpcode.APPRAISAL_INFO_EVENT)
                            {
                                var message = CM_Examine.SetAppraiseInfo.read(binaryReader);

                                if (message.i_objid == loginEvent.Biota.Id)
                                    ACEBiotaCreator.Update(message, loginEvent.Biota, rwLock);

                                // If this is an inventory item, update it
                                if (loginEvent.WorldObjects.TryGetValue(message.i_objid, out var value))
                                {
                                    ACEBiotaCreator.Update(message, value.Biota, rwLock);
                                    value.AppraiseInfoReceived = true;
                                }
                            }
                        }

                        if (messageCode == (uint)PacketOpcode.Evt_Physics__CreateObject_ID)
                        {
                            var message = CM_Physics.CreateObject.read(binaryReader);

                            // We only process player create/update messages for player biotas during the login process
                            if (!loginEvent.PlayerLoginCompleted)
                            {
                                if (message.object_id == loginEvent.Biota.Id)
                                {
                                    ACEBiotaCreator.Update(message, loginEvent.Biota, rwLock, true);

                                    var position = new ACE.Entity.Position(message.physicsdesc.pos.objcell_id, message.physicsdesc.pos.frame.m_fOrigin.x, message.physicsdesc.pos.frame.m_fOrigin.y, message.physicsdesc.pos.frame.m_fOrigin.z, message.physicsdesc.pos.frame.qx, message.physicsdesc.pos.frame.qy, message.physicsdesc.pos.frame.qz, message.physicsdesc.pos.frame.qw);
                                    loginEvent.Biota.SetPosition(ACE.Entity.Enum.Properties.PositionType.Location, position, rwLock, out _);
                                }
                            }

                            // Record inventory items
                            if (!loginEvent.WorldObjects.ContainsKey(message.object_id) && loginEvent.IsPossedItem(message.object_id))
                            {
                                var item = new WorldObjectItem(message.object_id, message.wdesc._name.m_buffer);
                                ACEBiotaCreator.Update(message, item.Biota, rwLock, true);
                                loginEvent.WorldObjects[message.object_id] = item;
                            }
                        }
                    }
                }
                catch (InvalidDataException)
                {
                    // This is a pcap parse error
                }
                catch (Exception)
                {
                    messageExceptions++;
                    // Do something with the exception maybe
                }
            }

            return (hits, messageExceptions);
        }

        public override void WriteOutput(string destinationRoot, ref bool writeOuptputAborted)
        {
            var playerExportsFolder = Path.Combine(destinationRoot, "Player Exports");

            if (!Directory.Exists(playerExportsFolder))
                Directory.CreateDirectory(playerExportsFolder);


            var notes = new StringBuilder();
            notes.AppendLine("The following command will import all the sql files into your retail shard. It can take many hours");
            notes.AppendLine("for /f \"delims=\" %f in ('dir /b /s \"c:\\Output\\Player Exports\\*.sql\"') do mysql --user=root --password=password ace_shard_retail < \"%f\"");

            // Find guid collisions across servers
            Dictionary<string, HashSet<uint>> guidsByServer = new Dictionary<string, HashSet<uint>>();
            foreach (var server in playersByServer)
            {
                var guids = new HashSet<uint>();
                guidsByServer.Add(server.Key, guids);

                foreach (var player in server.Value)
                {
                    guids.Add(player.Key);
                    foreach (var loginEvent in player.Value.LoginEvents)
                    {
                        foreach (var wo in loginEvent.WorldObjects)
                            guids.Add(wo.Key);
                    }
                }
            }

            var keys = guidsByServer.Keys.ToList();
            for (int i = 0; i < keys.Count - 1; i++)
            {
                for (int j = i + 1; j < keys.Count; j++)
                {
                    var intersections = new HashSet<uint>(guidsByServer[keys[i]]);
                    intersections.IntersectWith(guidsByServer[keys[j]]);
                    if (intersections.Count > 0)
                    {
                        notes.AppendLine();
                        notes.AppendLine(keys[i] + " IntersectWith " + keys[j]);
                        foreach (var intersect in intersections)
                            notes.AppendLine(intersect.ToString("X8"));
                    }
                }
            }

            var notesFileName = Path.Combine(playerExportsFolder, "notes.txt");
            File.WriteAllText(notesFileName, notes.ToString());


            var biotaWriter = new ACE.Database.SQLFormatters.Shard.BiotaSQLWriter();
            var characterWriter = new ACE.Database.SQLFormatters.Shard.CharacterSQLWriter();

            var rwLock = new ReaderWriterLockSlim();

            foreach (var server in playersByServer)
            {
                var serverDirectory = Path.Combine(playerExportsFolder, server.Key);

                foreach (var player in server.Value)
                {
                    if (writeOuptputAborted)
                        return;

                    // We only export the last login event
                    var loginEvent = player.Value.LoginEvents.Last();

                    // biota is corrupt
                    if (loginEvent.Biota.BiotaPropertiesDID.Count == 0)
                        continue;

                    var name = loginEvent.Biota.GetProperty(ACE.Entity.Enum.Properties.PropertyString.Name);

                    var playerDirectory = Path.Combine(serverDirectory, name);

                    var loginEventDirectory = Path.Combine(playerDirectory, loginEvent.TSec.ToString());

                    if (!Directory.Exists(loginEventDirectory))
                        Directory.CreateDirectory(loginEventDirectory);

                    var sb = new StringBuilder();

                    sb.AppendLine("Source: " + loginEvent.FileName);
                    sb.AppendLine();

                    // Biota
                    {
                        var defaultFileName = biotaWriter.GetDefaultFileName(loginEvent.Biota);

                        var fileName = Path.Combine(loginEventDirectory, defaultFileName);

                        loginEvent.Biota.WeenieType = (int)ACEBiotaCreator.DetermineWeenieType(loginEvent.Biota, rwLock);

                        SetBiotaPopulatedCollections(loginEvent.Biota);

                        using (StreamWriter outputFile = new StreamWriter(fileName, false))
                            biotaWriter.CreateSQLINSERTStatement(loginEvent.Biota, outputFile);
                    }

                    // Character
                    {
                        loginEvent.Character.Name = name;

                        var defaultFileName = loginEvent.Character.Id.ToString("X8") + " " + name + " - Character.sql";

                        var fileName = Path.Combine(loginEventDirectory, defaultFileName);

                        using (StreamWriter outputFile = new StreamWriter(fileName, false))
                            characterWriter.CreateSQLINSERTStatement(loginEvent.Character, outputFile);
                    }

                    // Posessions
                    foreach (var woi in loginEvent.WorldObjects)
                    {
                        var woiBeingUsed = woi.Value;

                        // If we don't have appraise info for this WO, try to find one that does
                        if (!woi.Value.AppraiseInfoReceived)
                        {
                            var result = player.Value.GetBestPossession(woi.Key, woi.Value.Name);

                            if (result != woiBeingUsed)
                            {
                                // todo log that the item was replaced with a better match from a different session
                                woiBeingUsed = result;
                            }
                        }

                        // Update the PlacementPosition and Container
                        for (int i = 0; i < loginEvent.Inventory.Count ; i++)
                        {
                            if (loginEvent.Inventory[i].guid == woiBeingUsed.Biota.Id)
                            {
                                woiBeingUsed.Biota.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.PlacementPosition, i, rwLock, out _);
                                woiBeingUsed.Biota.SetProperty(ACE.Entity.Enum.Properties.PropertyInstanceId.Container, loginEvent.Biota.Id, rwLock, out _);
                                goto processed;
                            }
                        }
                        foreach (var container in loginEvent.ViewContentsEvents)
                        {
                            var index = container.Value.IndexOf(woiBeingUsed.Biota.Id);
                            if (index != -1)
                            {
                                woiBeingUsed.Biota.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.PlacementPosition, index, rwLock, out _);
                                woiBeingUsed.Biota.SetProperty(ACE.Entity.Enum.Properties.PropertyInstanceId.Container, container.Key, rwLock, out _);
                                goto processed;
                            }
                        }
                        processed:

                        var defaultFileName = biotaWriter.GetDefaultFileName(woiBeingUsed.Biota);

                        defaultFileName = String.Concat(defaultFileName.Split(Path.GetInvalidFileNameChars()));

                        var fileName = Path.Combine(loginEventDirectory, defaultFileName);

                        woiBeingUsed.Biota.WeenieType = (int)ACEBiotaCreator.DetermineWeenieType(woiBeingUsed.Biota, rwLock);

                        if (woiBeingUsed.Biota.WeenieType == 0)
                        {
                            sb.AppendLine($"{woiBeingUsed.Biota.Id:X8}:{woiBeingUsed.Name} failed to determine weenie type and was not exported.");
                            continue;
                        }

                        if (!woiBeingUsed.AppraiseInfoReceived)
                            sb.AppendLine($"{woiBeingUsed.Biota.Id:X8}:{woiBeingUsed.Name} did not receive full appraisal info. Item has incomplete data.");

                        SetBiotaPopulatedCollections(woiBeingUsed.Biota);

                        using (StreamWriter outputFile = new StreamWriter(fileName, false))
                            biotaWriter.CreateSQLINSERTStatement(woiBeingUsed.Biota, outputFile);
                    }

                    // Determine missing possessions
                    var possessions = new HashSet<uint>();
                    foreach (var value in loginEvent.Inventory)
                        possessions.Add(value.guid);
                    foreach (var value in loginEvent.Equipment)
                        possessions.Add(value.guid);
                    foreach (var container in loginEvent.ViewContentsEvents)
                    {
                        if (possessions.Contains(container.Key))
                        {
                            foreach (var child in container.Value)
                                possessions.Add(child);
                        }
                    }

                    sb.AppendLine();

                    foreach (var value in possessions)
                    {
                        if (!loginEvent.WorldObjects.ContainsKey(value))
                            sb.AppendLine($"{value:X8} is a possessed item but was not found");
                    }


                    var resutlsFileName = Path.Combine(loginEventDirectory, "results.txt");
                    File.WriteAllText(resutlsFileName, sb.ToString());
                }
            }
        }


        [Flags]
        enum PopulatedCollectionFlags
        {
            BiotaPropertiesAnimPart = 0x1,
            BiotaPropertiesAttribute = 0x2,
            BiotaPropertiesAttribute2nd = 0x4,
            BiotaPropertiesBodyPart = 0x8,
            BiotaPropertiesBook = 0x10,
            BiotaPropertiesBookPageData = 0x20,
            BiotaPropertiesBool = 0x40,
            BiotaPropertiesCreateList = 0x80,
            BiotaPropertiesDID = 0x100,
            BiotaPropertiesEmote = 0x200,
            BiotaPropertiesEnchantmentRegistry = 0x400,
            BiotaPropertiesEventFilter = 0x800,
            BiotaPropertiesFloat = 0x1000,
            BiotaPropertiesGenerator = 0x2000,
            BiotaPropertiesIID = 0x4000,
            BiotaPropertiesInt = 0x8000,
            BiotaPropertiesInt64 = 0x10000,
            BiotaPropertiesPalette = 0x20000,
            BiotaPropertiesPosition = 0x40000,
            BiotaPropertiesSkill = 0x80000,
            BiotaPropertiesSpellBook = 0x100000,
            BiotaPropertiesString = 0x200000,
            BiotaPropertiesTextureMap = 0x400000,
            HousePermission = 0x800000,
        }

        // We just copy the function over here.
        // If we call the one in ACE.Database, we need to add nuget packages log4net, EntityFrameworkCore, etc..
        private static void SetBiotaPopulatedCollections(Biota biota)
        {
            PopulatedCollectionFlags populatedCollectionFlags = 0;

            if (biota.BiotaPropertiesAnimPart != null && biota.BiotaPropertiesAnimPart.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAnimPart;
            if (biota.BiotaPropertiesAttribute != null && biota.BiotaPropertiesAttribute.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAttribute;
            if (biota.BiotaPropertiesAttribute2nd != null && biota.BiotaPropertiesAttribute2nd.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesAttribute2nd;
            if (biota.BiotaPropertiesBodyPart != null && biota.BiotaPropertiesBodyPart.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBodyPart;
            if (biota.BiotaPropertiesBook != null) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBook;
            if (biota.BiotaPropertiesBookPageData != null && biota.BiotaPropertiesBookPageData.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBookPageData;
            if (biota.BiotaPropertiesBool != null && biota.BiotaPropertiesBool.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesBool;
            if (biota.BiotaPropertiesCreateList != null && biota.BiotaPropertiesCreateList.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesCreateList;
            if (biota.BiotaPropertiesDID != null && biota.BiotaPropertiesDID.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesDID;
            if (biota.BiotaPropertiesEmote != null && biota.BiotaPropertiesEmote.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesEmote;
            if (biota.BiotaPropertiesEnchantmentRegistry != null && biota.BiotaPropertiesEnchantmentRegistry.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesEnchantmentRegistry;
            if (biota.BiotaPropertiesEventFilter != null && biota.BiotaPropertiesEventFilter.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesEventFilter;
            if (biota.BiotaPropertiesFloat != null && biota.BiotaPropertiesFloat.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesFloat;
            if (biota.BiotaPropertiesGenerator != null && biota.BiotaPropertiesGenerator.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesGenerator;
            if (biota.BiotaPropertiesIID != null && biota.BiotaPropertiesIID.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesIID;
            if (biota.BiotaPropertiesInt != null && biota.BiotaPropertiesInt.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesInt;
            if (biota.BiotaPropertiesInt64 != null && biota.BiotaPropertiesInt64.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesInt64;
            if (biota.BiotaPropertiesPalette != null && biota.BiotaPropertiesPalette.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesPalette;
            if (biota.BiotaPropertiesPosition != null && biota.BiotaPropertiesPosition.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesPosition;
            if (biota.BiotaPropertiesSkill != null && biota.BiotaPropertiesSkill.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesSkill;
            if (biota.BiotaPropertiesSpellBook != null && biota.BiotaPropertiesSpellBook.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesSpellBook;
            if (biota.BiotaPropertiesString != null && biota.BiotaPropertiesString.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesString;
            if (biota.BiotaPropertiesTextureMap != null && biota.BiotaPropertiesTextureMap.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.BiotaPropertiesTextureMap;
            if (biota.HousePermission != null && biota.HousePermission.Count > 0) populatedCollectionFlags |= PopulatedCollectionFlags.HousePermission;

            biota.PopulatedCollectionFlags = (uint)populatedCollectionFlags;
        }
    }
}
