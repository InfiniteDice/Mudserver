using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MudCore.Actions
{
    /// <summary>
    /// Data transfer object for spell templates loaded from JSON.
    /// Used to display spell details when viewing pages in books.
    /// </summary>
    public class SpellTemplate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("requiredSkill")]
        public string RequiredSkill { get; set; } = "";

        [JsonPropertyName("minSkillLevel")]
        public int MinSkillLevel { get; set; } = 0;

        [JsonPropertyName("powerCost")]
        public int PowerCost { get; set; } = 0;

        [JsonPropertyName("castTimeSeconds")]
        public int CastTimeSeconds { get; set; } = 0;

        [JsonPropertyName("toolsRequired")]
        public List<string> ToolsRequired { get; set; } = new();

        [JsonPropertyName("roomFlags")]
        public List<string> RoomFlags { get; set; } = new();

        [JsonPropertyName("castingComponents")]
        public List<CastingComponent> CastingComponents { get; set; } = new();

        [JsonPropertyName("spellEffects")]
        public List<SpellEffect> SpellEffects { get; set; } = new();

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

            [JsonPropertyName("target")]
            public string Target { get; set; } = "";

            [JsonPropertyName("dmgPerLevel")]
            public int DmgPerLevel { get; set; } = 0;

            [JsonPropertyName("damageType")]
            public string DamageType { get; set; } = "";

            [JsonPropertyName("effectDuration")]
            public int EffectDuration { get; set; } = 0;
        }
    }
}