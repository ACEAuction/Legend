﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ACE.Entity.Models;

namespace ACE.Mods.Legend.Lib.Auction.Models;

public class CreateSellOrder
{
    public uint ItemId { get; set; }
    public uint ItemIconId { get; set; }
    public string ItemInfo { get; set; }
    public uint SellerId { get; set; }
    public string SellerName { get; set; } 
    public uint CurrencyWcid { get; internal set; }
    public string CurrencyName { get; set; }
    public uint CurrencyIconId { get; set; }
    public uint StartPrice { get; set; }
    public uint NumberOfStacks { get; internal set; }
    public uint StackSize { get; internal set; }
    public uint BuyoutPrice { get; set; }
    public uint HoursDuration { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
}

