using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MudCore.Actions
{
    /// <summary>
    /// Data transfer object for spell templates loaded from JSON.
    /// Used to display spell details when viewing pages in books.
    /// JSON spell files are the source of truth; null values are ignored via null checks.
    /// </summary>
    public class SpellTemplate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("requiredSkill")]
        public string RequiredSkill { get; set; } = "";

        [JsonPropertyName("minSkillLevel")]
        public int MinSkillLevel { get; set; } = 0;

        [JsonPropertyName("alignmentRestriction")]
        public string? AlignmentRestriction { get; set; }

        [JsonPropertyName("powerCost")]
        public int PowerCost { get; set; } = 0;

        [JsonPropertyName("castTimeSeconds")]
        public int CastTimeSeconds { get; set; } = 0;

        [JsonPropertyName("deliveryMethod")]
        public string? DeliveryMethod { get; set; }

        [JsonPropertyName("toolsRequired")]
        public List<string> ToolsRequired { get; set; } = new();

        [JsonPropertyName("roomFlags")]
        public List<string> RoomFlags { get; set; } = new();

        [JsonPropertyName("notRoomFlags")]
        public List<string>? NotRoomFlags { get; set; }

        [JsonPropertyName("container")]
        public List<string>? Container { get; set; }

        [JsonPropertyName("areaClimate")]
        public List<string?>? AreaClimate { get; set; }

        [JsonPropertyName("canChain")]
        public bool CanChain { get; set; } = false;

        [JsonPropertyName("chainChance")]
        public double? ChainChance { get; set; }

        [JsonPropertyName("chainLimit")]
        public int? ChainLimit { get; set; }

        [JsonPropertyName("roomRange")]
        public int? RoomRange { get; set; }

        [JsonPropertyName("ritualOnly")]
        public bool RitualOnly { get; set; } = false;

        [JsonPropertyName("ritualParticipants")]
        public int? RitualParticipants { get; set; }

        [JsonPropertyName("allowSubstitutions")]
        public bool AllowSubstitutions { get; set; } = false;

        [JsonPropertyName("castingComponents")]
        public List<CastingComponent> CastingComponents { get; set; } = new();

        [JsonPropertyName("spellEffects")]
        public List<SpellEffect> SpellEffects { get; set; } = new();

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        public class CastingComponent
        {
            [JsonPropertyName("template")]
            public string Template { get; set; } = "";

            [JsonPropertyName("qty")]
            public int Qty { get; set; } = 1;
        }

        public class SpellEffect
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "";

            // --- legacy field, kept for backward compatibility ---
            [JsonPropertyName("target")]
            public string? Target { get; set; }

            [JsonPropertyName("effectTarget")]
            public string? EffectTarget { get; set; }

            [JsonPropertyName("distantTarget")]
            public string? DistantTarget { get; set; }

            [JsonPropertyName("distanceRooms")]
            public int? DistanceRooms { get; set; }

            [JsonPropertyName("baseValue")]
            public int? BaseValue { get; set; }

            // --- legacy field, kept for backward compatibility ---
            [JsonPropertyName("dmgPerLevel")]
            public int DmgPerLevel { get; set; } = 0;

            [JsonPropertyName("valuePerLevel")]
            public int? ValuePerLevel { get; set; }

            [JsonPropertyName("statModifier")]
            public string? StatModifier { get; set; }

            [JsonPropertyName("statModifies")]
            public string? StatModifies { get; set; }

            [JsonPropertyName("damageType")]
            public string? DamageType { get; set; }

            /// <summary>           
            /// Null means instantaneous or not applicable.
            /// </summary>
            [JsonPropertyName("durationSeconds")]
            public int? DurationSeconds { get; set; }

            public int EffectDuration { get; set; } = 0;

            [JsonPropertyName("flagEffect")]
            public string? FlagEffect { get; set; }

            [JsonPropertyName("chance")]
            public double? Chance { get; set; }

            // ===== NEW: Reusable choice prompt system =====
            // Spells that require player interaction before casting can use this pattern.
            // Example: Grow spell asks "Choose +1 or +2 size"
            // The choice is stored in PendingAction.Data[choiceFieldName] and passed to DeliverXxx() method.
            // See spell_grow.json and CommandHandler.HandleChoiceResponse() for implementation pattern.
            [JsonPropertyName("hasChoicePrompt")]
            public bool HasChoicePrompt { get; set; } = false;

            [JsonPropertyName("choicePromptText")]
            public string? ChoicePromptText { get; set; }

            [JsonPropertyName("choiceOptions")]
            public List<int>? ChoiceOptions { get; set; }

            [JsonPropertyName("choiceFieldName")]
            public string? ChoiceFieldName { get; set; }

            [JsonPropertyName("durationModifier")]
            public double DurationModifier { get; set; } = 1.0;
        }
    }
}