using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PostHelpWantedAds
{
    internal class ModData
    {
        public bool DidPostAd { get; set; } = false;
        public int DaysSincePost { get; set; } = 0;
        public string ItemIdStr { get; set; } = "";
        public string ChosenVillager { get; set; } = "";
        public string PostedItem { get; set; } = "";
        public string ActiveQuestData { get; set; } = "";
        public string ActiveQuestId { get; set; } = "";

    }
}
