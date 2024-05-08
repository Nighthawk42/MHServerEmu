﻿using System.Text;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Serialization;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.GameData.Tables;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Social.Guilds;

namespace MHServerEmu.Games.Entities.Avatars
{
    public class Avatar : Agent
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private ReplicatedVariable<string> _playerName = new(0, string.Empty);
        private ulong _ownerPlayerDbId;
        private List<AbilityKeyMapping> _abilityKeyMappingList = new();

        private ulong _guildId = GuildMember.InvalidGuildId;
        private string _guildName = string.Empty;
        private GuildMembership _guildMembership = GuildMembership.eGMNone;

        public string PlayerName { get => _playerName.Value; }
        public ulong OwnerPlayerDbId { get => _ownerPlayerDbId; }
        public AbilityKeyMapping CurrentAbilityKeyMapping { get => _abilityKeyMappingList.FirstOrDefault(); }

        public Agent CurrentTeamUpAgent { get; set; } = null;

        public AvatarPrototype AvatarPrototype { get => EntityPrototype as AvatarPrototype; }
        public int PrestigeLevel { get => Properties[PropertyEnum.AvatarPrestigeLevel]; }

        public override bool IsMovementAuthoritative => false;
        public override bool CanBeRepulsed => false;
        public override bool CanRepulseOthers => false;

        // new
        public Avatar(Game game) : base(game) { }

        // old
        public Avatar(ulong entityId, ulong replicationId) : base(new EntityBaseData())
        {
            // Entity
            BaseData.ReplicationPolicy = AOINetworkPolicyValues.AOIChannelOwner;
            BaseData.LocomotionState = new(0f);
            BaseData.EntityId = entityId;
            BaseData.InterestPolicies = AOINetworkPolicyValues.AOIChannelOwner;
            BaseData.FieldFlags = EntityCreateMessageFlags.HasInterestPolicies | EntityCreateMessageFlags.HasInvLoc | EntityCreateMessageFlags.HasAvatarWorldInstanceId;
            
            ReplicationPolicy = AOINetworkPolicyValues.AOIChannelOwner;
            Properties = new(replicationId);

            // WorldEntity
            _trackingContextMap = new();
            _conditionCollection = new(this);
            _powerCollection = new(this);
            _unkEvent = 134463198;

            // Avatar
            _playerName = new(++replicationId, string.Empty);
            _ownerPlayerDbId = 0x20000000000D3D03;   // D3D03 == 867587 from Player's EntityBaseData
        }

        public Avatar(EntityBaseData baseData, ByteString archiveData) : base(baseData, archiveData) { }

        public Avatar(EntityBaseData baseData, EntityTrackingContextMap trackingContextMap, ConditionCollection conditionCollection, PowerCollection powerCollection, int unkEvent,
            ReplicatedVariable<string> playerName, ulong ownerPlayerDbId, ulong guildId, string guildName, GuildMembership guildMembership, IEnumerable<AbilityKeyMapping> abilityKeyMappings)
            : base(baseData)
        {
            _trackingContextMap = trackingContextMap;
            _conditionCollection = conditionCollection;
            _powerCollection = powerCollection;
            _unkEvent = unkEvent;

            _playerName = playerName;
            _ownerPlayerDbId = ownerPlayerDbId;
            _guildId = guildId;
            _guildName = guildName;
            _guildMembership = guildMembership;
            _abilityKeyMappingList.AddRange(abilityKeyMappings);
        }

        public override bool Serialize(Archive archive)
        {
            bool success = base.Serialize(archive);

            success &= Serializer.Transfer(archive, ref _playerName);
            success &= Serializer.Transfer(archive, ref _ownerPlayerDbId);

            // There is an unused string here that is always empty
            string emptyString = string.Empty;
            success &= Serializer.Transfer(archive, ref emptyString);
            if (emptyString != string.Empty)
                Logger.Warn($"Serialize(): emptyString is not empty!");

            //if (archive.IsReplication)
            success &= GuildMember.SerializeReplicationRuntimeInfo(archive, ref _guildId, ref _guildName, ref _guildMembership);

            success &= Serializer.Transfer(archive, ref _abilityKeyMappingList);

            return success;
        }

        protected override void Decode(CodedInputStream stream)
        {
            base.Decode(stream);

            BoolDecoder boolDecoder = new();

            _playerName.Decode(stream);
            _ownerPlayerDbId = stream.ReadRawVarint64();

            // Similar throwaway string to Player entity
            if (stream.ReadRawString() != string.Empty)
                Logger.Warn($"Decode(): emptyString is not empty!");

            GuildMember.SerializeReplicationRuntimeInfo(stream, boolDecoder, ref _guildId, ref _guildName, ref _guildMembership);

            _abilityKeyMappingList.Clear();
            uint numAbilityKeyMappings = stream.ReadRawVarint32();
            for (uint i = 0; i < numAbilityKeyMappings; i++)
            {
                AbilityKeyMapping abilityKeyMapping = new();
                abilityKeyMapping.Decode(stream, boolDecoder);
                _abilityKeyMappingList.Add(abilityKeyMapping);
            }
        }

        public override void Encode(CodedOutputStream stream)
        {
            base.Encode(stream);

            // Prepare bool encoder
            BoolEncoder boolEncoder = new();

            boolEncoder.EncodeBool(_guildId != GuildMember.InvalidGuildId);
            foreach (AbilityKeyMapping keyMap in _abilityKeyMappingList)
                keyMap.EncodeBools(boolEncoder);

            boolEncoder.Cook();

            // Encode
            _playerName.Encode(stream);
            stream.WriteRawVarint64(_ownerPlayerDbId);

            stream.WriteRawString(string.Empty);    // throwaway string

            GuildMember.SerializeReplicationRuntimeInfo(stream, boolEncoder, ref _guildId, ref _guildName, ref _guildMembership);
            
            stream.WriteRawVarint64((uint)_abilityKeyMappingList.Count);
            foreach (AbilityKeyMapping keyMap in _abilityKeyMappingList)
                keyMap.Encode(stream, boolEncoder);
        }

        /// <summary>
        /// Initializes this <see cref="Avatar"/> from data contained in the provided <see cref="DBAccount"/>.
        /// </summary>
        public void InitializeFromDBAccount(PrototypeId prototypeId, DBAccount account)
        {
            DBAvatar dbAvatar = account.GetAvatar((long)prototypeId);
            AvatarPrototype prototype = GameDatabase.GetPrototype<AvatarPrototype>(prototypeId);

            // Base Data
            BaseData.PrototypeId = prototypeId;

            // Archive Data
            _playerName.Value = account.PlayerName;

            // Properties
            Properties.FlattenCopyFrom(prototype.Properties, true);

            // AvatarLastActiveTime is needed for missions to show up in the tracker
            Properties[PropertyEnum.AvatarLastActiveCalendarTime] = 1509657924421;  // Nov 02 2017 21:25:24 GMT+0000
            Properties[PropertyEnum.AvatarLastActiveTime] = 161351646299;

            Properties[PropertyEnum.CostumeCurrent] = dbAvatar.RawCostume;
            Properties[PropertyEnum.CharacterLevel] = 60;
            Properties[PropertyEnum.CombatLevel] = 60;
            Properties[PropertyEnum.AvatarPowerUltimatePoints] = 19;

            // Health
            Properties[PropertyEnum.HealthMaxOther] = (int)(float)Properties[PropertyEnum.HealthBase];
            Properties[PropertyEnum.Health] = Properties[PropertyEnum.HealthMaxOther];

            // Resources
            // Ger primary resources defaults from PrimaryResourceBehaviors
            foreach (PrototypeId manaBehaviorId in prototype.PrimaryResourceBehaviors)
            {
                var behaviorPrototype = GameDatabase.GetPrototype<PrimaryResourceManaBehaviorPrototype>(manaBehaviorId);
                Curve manaCurve = GameDatabase.GetCurve(behaviorPrototype.BaseEndurancePerLevel);
                Properties[PropertyEnum.EnduranceBase, (int)behaviorPrototype.ManaType] = manaCurve.GetAt(60);
            }
;           
            // Set primary resources
            Properties[PropertyEnum.EnduranceMaxOther] = Properties[PropertyEnum.EnduranceBase];
            Properties[PropertyEnum.EnduranceMax] = Properties[PropertyEnum.EnduranceMaxOther];
            Properties[PropertyEnum.Endurance] = Properties[PropertyEnum.EnduranceMax];
            Properties[PropertyEnum.EnduranceMaxOther, (int)ManaType.Type2] = Properties[PropertyEnum.EnduranceBase, (int)ManaType.Type2];
            Properties[PropertyEnum.EnduranceMax, (int)ManaType.Type2] = Properties[PropertyEnum.EnduranceMaxOther, (int)ManaType.Type2];
            Properties[PropertyEnum.Endurance, (int)ManaType.Type2] = Properties[PropertyEnum.EnduranceMax, (int)ManaType.Type2];

            // Secondary resource base is already present in the prototype's property collection as a curve property
            Properties[PropertyEnum.SecondaryResourceMax] = Properties[PropertyEnum.SecondaryResourceMaxBase];
            Properties[PropertyEnum.SecondaryResource] = Properties[PropertyEnum.SecondaryResourceMax];

            // Stats
            foreach (PrototypeId entryId in prototype.StatProgressionTable)
            {
                var entry = entryId.As<StatProgressionEntryPrototype>();

                if (entry.DurabilityValue > 0)
                    Properties[PropertyEnum.StatDurability] = entry.DurabilityValue;
                
                if (entry.StrengthValue > 0)
                    Properties[PropertyEnum.StatStrength] = entry.StrengthValue;
                
                if (entry.FightingSkillsValue > 0)
                    Properties[PropertyEnum.StatFightingSkills] = entry.FightingSkillsValue;
                
                if (entry.SpeedValue > 0)
                    Properties[PropertyEnum.StatSpeed] = entry.SpeedValue;
                
                if (entry.EnergyProjectionValue > 0)
                    Properties[PropertyEnum.StatEnergyProjection] = entry.EnergyProjectionValue;
                
                if (entry.IntelligenceValue > 0)
                    Properties[PropertyEnum.StatIntelligence] = entry.IntelligenceValue;
            }

            // Unlock all stealable powers for Rogue
            if (prototypeId == (PrototypeId)6514650100102861856)
            {
                foreach (PrototypeId stealablePowerInfoRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<StealablePowerInfoPrototype>(PrototypeIterateFlags.NoAbstract))
                {
                    var stealablePowerInfo = stealablePowerInfoRef.As<StealablePowerInfoPrototype>();
                    Properties[PropertyEnum.StolenPowerAvailable, stealablePowerInfo.Power] = true;
                }
            }

            // We need 10 synergies active to remove the in-game popup
            int synergyCount = 0;
            foreach (PrototypeId avatarRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                Properties[PropertyEnum.AvatarSynergySelected, avatarRef] = true;
                if (++synergyCount >= 10) break;
            }

            // Initialize AbilityKeyMapping
            _abilityKeyMappingList.Clear();
            AbilityKeyMapping abilityKeyMapping = new();
            if (dbAvatar.RawAbilityKeyMapping != null)
            {
                // Deserialize existing saved mapping if there is one
                using (Archive archive = new(ArchiveSerializeType.Database, dbAvatar.RawAbilityKeyMapping))
                    abilityKeyMapping.Serialize(archive);
            }
            else
            {
                // Initialize a new mapping
                abilityKeyMapping.SlotDefaultAbilities(this);
            }

            _abilityKeyMappingList.Add(abilityKeyMapping);
        }

        public PrototypeId GetOriginalPowerFromMappedPower(PrototypeId mappedPowerRef)
        {
            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.AvatarMappedPower))
            {
                if ((PrototypeId)kvp.Value != mappedPowerRef) continue;
                Property.FromParam(kvp.Key, 0, out PrototypeId originalPower);
                return originalPower;
            }

            return PrototypeId.Invalid;
        }

        public PrototypeId GetMappedPowerFromOriginalPower(PrototypeId originalPowerRef)
        {
            foreach (var kvp in Properties.IteratePropertyRange(PropertyEnum.AvatarMappedPower, originalPowerRef))
            {
                PrototypeId mappedPowerRef = kvp.Value;

                if (mappedPowerRef == PrototypeId.Invalid)
                    Logger.Warn("GetMappedPowerFromOriginalPower(): mappedPowerRefTemp == PrototypeId.Invalid");

                return mappedPowerRef;
            }

            return PrototypeId.Invalid;
        }

        public override bool HasPowerInPowerProgression(PrototypeId powerRef)
        {
            if (GameDataTables.Instance.PowerOwnerTable.GetPowerProgressionEntry(PrototypeDataRef, powerRef) != null)
                return true;

            if (GameDataTables.Instance.PowerOwnerTable.GetTalentEntry(PrototypeDataRef, powerRef) != null)
                return true;

            return false;
        }

        public override bool GetPowerProgressionInfo(PrototypeId powerProtoRef, out PowerProgressionInfo info)
        {
            info = new();

            if (powerProtoRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, "GetPowerProgressionInfo(): powerProtoRef == PrototypeId.Invalid");

            AvatarPrototype avatarProto = AvatarPrototype;
            if (avatarProto == null)
                return Logger.WarnReturn(false, "GetPowerProgressionInfo(): avatarProto == null");

            PrototypeId progressionInfoPower = powerProtoRef;
            PrototypeId mappedPowerRef;

            // Check if this is a mapped power
            PrototypeId originalPowerRef = GetOriginalPowerFromMappedPower(powerProtoRef);
            if (originalPowerRef != PrototypeId.Invalid)
            {
                mappedPowerRef = powerProtoRef;
                progressionInfoPower = originalPowerRef;
            }
            else
            {
                mappedPowerRef = GetMappedPowerFromOriginalPower(powerProtoRef);
            }

            PowerOwnerTable powerOwnerTable = GameDataTables.Instance.PowerOwnerTable;

            // Initialize info
            // Case 1 - Progression Power
            PowerProgressionEntryPrototype powerProgressionEntry = powerOwnerTable.GetPowerProgressionEntry(avatarProto.DataRef, progressionInfoPower);
            if (powerProgressionEntry != null)
            {
                PrototypeId powerTabRef = powerOwnerTable.GetPowerProgressionTab(avatarProto.DataRef, progressionInfoPower);
                if (powerTabRef == PrototypeId.Invalid) return Logger.WarnReturn(false, "GetPowerProgressionInfo(): powerTabRef == PrototypeId.Invalid");

                info.InitForAvatar(powerProgressionEntry, mappedPowerRef, powerTabRef);
                return info.IsValid;
            }

            // Case 2 - Talent
            var talentEntryPair = powerOwnerTable.GetTalentEntryPair(avatarProto.DataRef, progressionInfoPower);
            var talentGroupPair = powerOwnerTable.GetTalentGroupPair(avatarProto.DataRef, progressionInfoPower);
            if (talentEntryPair.Item1 != null && talentGroupPair.Item1 != null)
            {
                info.InitForAvatar(talentEntryPair.Item1, talentGroupPair.Item1, talentEntryPair.Item2, talentGroupPair.Item2);
                return info.IsValid;
            }

            // Case 3 - Non-Progression Power
            info.InitNonProgressionPower(powerProtoRef);
            return info.IsValid;
        }

        public long GetInfinityPointsSpentOnBonus(PrototypeId infinityGemBonusRef, bool getTempPoints)
        {
            if (getTempPoints)
            {
                long pointsSpent = Properties[PropertyEnum.InfinityPointsSpentTemp, infinityGemBonusRef];
                if (pointsSpent >= 0) return pointsSpent;
            }

            return Properties[PropertyEnum.InfinityPointsSpentTemp, infinityGemBonusRef];
        }

        public int GetOmegaPointsSpentOnBonus(PrototypeId omegaBonusRef, bool getTempPoints)
        {
            if (getTempPoints)
            {
                int pointsSpent = Properties[PropertyEnum.OmegaSpecTemp, omegaBonusRef];
                if (pointsSpent >= 0) return pointsSpent;
            }

            return Properties[PropertyEnum.OmegaSpec, omegaBonusRef];
        }

        protected override void BuildString(StringBuilder sb)
        {
            base.BuildString(sb);

            sb.AppendLine($"{nameof(_playerName)}: {_playerName}");
            sb.AppendLine($"{nameof(_ownerPlayerDbId)}: 0x{OwnerPlayerDbId:X}");

            if (_guildId != GuildMember.InvalidGuildId)
            {
                sb.AppendLine($"{nameof(_guildId)}: {_guildId}");
                sb.AppendLine($"{nameof(_guildName)}: {_guildName}");
                sb.AppendLine($"{nameof(_guildMembership)}: {_guildMembership}");
            }

            for (int i = 0; i < _abilityKeyMappingList.Count; i++)
                sb.AppendLine($"{nameof(_abilityKeyMappingList)}[{i}]: {_abilityKeyMappingList[i]}");
        }
    }
}
