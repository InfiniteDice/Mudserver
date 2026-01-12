using System;
using System.Collections.Generic;
using MudCore.Entities; // Item, Player types referenced in helpers

namespace MudCore.Entities
{
    /// <summary>
    /// Value-type representation of currency: Gold / Silver / Copper.
    /// Internally operations use copper (long) as canonical unit.
    /// </summary>
    public readonly struct Currency : IComparable<Currency>
    {
        public const int CopperPerSilver = 100;
        public const int SilverPerGold = 20;
        public const long CopperPerGold = (long)CopperPerSilver * SilverPerGold;

        public int Gold { get; }
        public int Silver { get; }
        public int Copper { get; }

        public Currency(int gold, int silver, int copper)
        {
            // normalize in ctor
            long total = ToCopperStatic(gold, silver, copper);
            var norm = FromCopper(total);
            Gold = norm.Gold;
            Silver = norm.Silver;
            Copper = norm.Copper;
        }

        public long TotalCopper => ToCopperStatic(Gold, Silver, Copper);

        public static long ToCopperStatic(int gold, int silver, int copper)
            => (long)gold * CopperPerGold + (long)silver * CopperPerSilver + copper;

        public long ToCopper() => TotalCopper;

        public static Currency FromCopper(long copper)
        {
            if (copper < 0) throw new ArgumentOutOfRangeException(nameof(copper));
            var gold = (int)(copper / CopperPerGold);
            copper -= (long)gold * CopperPerGold;
            var silver = (int)(copper / CopperPerSilver);
            copper -= (long)silver * CopperPerSilver;
            return new Currency(gold, silver, (int)copper, normalize: false);
        }

        // private ctor that skips normalization loop
        private Currency(int gold, int silver, int copper, bool normalize)
        {
            Gold = gold;
            Silver = silver;
            Copper = copper;
        }

        public Currency Add(Currency other) => FromCopper(this.TotalCopper + other.TotalCopper);
        public Currency Subtract(Currency other)
        {
            if (this.TotalCopper < other.TotalCopper) throw new InvalidOperationException("Result would be negative");
            return FromCopper(this.TotalCopper - other.TotalCopper);
        }

        public int CompareTo(Currency other) => TotalCopper.CompareTo(other.TotalCopper);
        public bool Covers(Currency price) => this.TotalCopper >= price.TotalCopper;

        /// <summary>
        /// Returns (change, owed). If paid >= price, change is non-zero and owed is zero.
        /// If paid < price, change is zero and owed is non-zero.
        /// </summary>
        public static (Currency change, Currency owed) CalculateChange(Currency price, Currency paid)
        {
            var paidCopper = paid.TotalCopper;
            var priceCopper = price.TotalCopper;
            if (paidCopper >= priceCopper)
            {
                return (FromCopper(paidCopper - priceCopper), FromCopper(0));
            }
            return (FromCopper(0), FromCopper(priceCopper - paidCopper));
        }

        // TODO: implement inventory helpers:
        // - static Currency FromItems(IEnumerable<Item> items)
        // - IEnumerable<Item> ToItems(ItemFactory factory)
        // - bool TryTakeFromInventory(Player player, Currency amount, out List<Item> consumed, SqliteConnection db)
    }
}