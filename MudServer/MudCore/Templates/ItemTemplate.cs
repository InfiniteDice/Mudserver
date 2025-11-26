using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MudCore.Templates
{
    public class ItemTemplate
    {
        // -----------------------
        // Identity
        // -----------------------
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        // -----------------------
        // Descriptions
        // -----------------------
        public string ShortDesc { get; set; } = "";
        public string LongDesc { get; set; } = "";

        // -----------------------
        // Classification
        // -----------------------
        public string Category { get; set; } = ""; // weapon, armor, container, consumable, tool, treasure, quest, imbued
        public string Material { get; set; } = "";
        public string Rarity { get; set; } = ""; // common, uncommon, rare, epic, legendary

        // -----------------------
        // Base stats
        // -----------------------
        public double? Weight { get; set; }
        public int? BaseValue { get; set; }

        // -----------------------
        // Flags (stored as array in JSON)
        // -----------------------
        public List<string> Flags { get; set; } = new();

        // -----------------------
        // Skill / crafting requirements
        // -----------------------
        public string SkillRequired { get; set; } = "";
        public int? SkillLevel { get; set; }

        // -----------------------
        // Wear / equip info
        // -----------------------
        public string WearLoc { get; set; } = ""; // head, body, legs, feet, hands, finger, neck, back, held, twohanded
        public int HandSlots { get; set; } = 1; // default 1, 0 = not applicable, 2 = two-handed

        // -----------------------
        // Weapon-specific
        // -----------------------
        public string DamageDice { get; set; } = ""; // e.g. "1d6"
        public string DamageType { get; set; } = ""; // slash, pierce, blunt, fire, ice, magic
        public double WeaponSpeed { get; set; } = 0.0;
        public int HitBonus { get; set; } = 0;

        // -----------------------
        // Armor-specific
        // -----------------------
        public int ArmorRating { get; set; } = 0;
        public string AbsorbType { get; set; } = ""; // physical, fire, ice, magic

        // -----------------------
        // Consumable / magical items
        // -----------------------
        public int MaxCharges { get; set; } = 0;
        public string SpellName { get; set; } = "";
        public string CastType { get; set; } = ""; // potion, scroll, wand, item

        // -----------------------
        // Container-specific
        // -----------------------
        public int MaxItems { get; set; } = 0;
        public double MaxWeight { get; set; } = 0.0;
        public string LockItemId { get; set; } = "";

        // -----------------------
        // Triggers & crafting components
        // -----------------------
        public List<string> Triggers { get; set; } = new(); // onwear, onuse, ondrop...
        public List<string> Components { get; set; } = new(); // required template IDs for crafting
    }
}
