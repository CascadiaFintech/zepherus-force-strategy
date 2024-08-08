#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.Cascadia
{
	public class ZepherusStrategy : Strategy
	{
		
		enum AlgoState {
			StartUp,
			WaitingForSignal,
			WaitingForTrigger,
			ATMOrderPending,
			WaitingForFill,
			WaitingForExit
		};
		
		private AlgoState m_state;
		
		private string  m_atm_strategy_id		= string.Empty;
		private string  m_order_id				= string.Empty;
		private bool	m_is_atm_created		= false;
		private bool    m_atm_order_pending    = false;
		private OrderAction m_order_action;
		private int m_trade_bar = 0;
	
		private NinjaTrader.NinjaScript.Indicators.RenkoKings.RenkoKings_ZephyrusForce RenkoKings_ZephyrusForce1;
		
		private double m_trend_direction;
		private int m_trades_in_direction = 0;
		
		
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Enter the description for your new custom Strategy here.";
				Name										= "Zepherus Force Strategy";
				Calculate									= Calculate.OnEachTick;
				EntriesPerDirection							= 1;
				EntryHandling								= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy				= true;
				ExitOnSessionCloseSeconds					= 30;
				IsFillLimitOnTouch							= false;
				MaximumBarsLookBack							= MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution							= OrderFillResolution.Standard;
				Slippage									= 0;
				StartBehavior								= StartBehavior.WaitUntilFlat;
				TimeInForce									= TimeInForce.Gtc;
				TraceOrders									= false;
				RealtimeErrorHandling						= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling							= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade							= 20;
				// Disable this property for performance gains in Strategy Analyzer optimizations
				// See the Help Guide for additional information
				IsInstantiatedOnEachOptimizationIteration	= true;
				
				TradesPerTrend = 1;
				TradeFIB = true;
				TradePA = true;
				TradeTrend = false;
				
				AtmStrategy = "None";
				TakeProfit = 0;
				StopLoss = 0;
				
				// Zepherus Force King Renko
				
				Z_FastPeriod = 31;
				Z_SlowPeriod = 89;
				Z_BandsPeriod = 20;
				Z_Neighborhood = 5;
				Z_FibonacciOffset = 5;
				Z_FibonacciMinDistance = 5;
				Z_FibonacciQualifying = 50;
				Z_FibonacciMinRetracement = 50;
				Z_FibonacciMaxRetracement = 79;
				Z_LevelQualifyingFlatAge = 5;
				Z_LevelAge = 50;
				Z_BrokenOnBodyTouch = false;
				Z_SignalSplitBars = 10;
				
			}
			else if (State == State.Configure)
			{
				m_trend_direction = 0;
				m_trades_in_direction = TradesPerTrend + 1;
				
				m_atm_strategy_id = string.Empty;
				m_order_id = string.Empty;
				m_is_atm_created  = false;
				m_atm_order_pending = false;
				m_trade_bar = 0;
				
				if(StopLoss != 0 && AtmStrategy == "None")
				{
					SetStopLoss(CalculationMode.Ticks, StopLoss);
				}
				if(TakeProfit != 0 && AtmStrategy == "None")
				{
					SetProfitTarget(CalculationMode.Ticks, TakeProfit);
				}
			}
			else if (State == State.DataLoaded)
			{				
				// Ninza suggested KingRenko
				RenkoKings_ZephyrusForce1				= RenkoKings_ZephyrusForce(
																Z_FastPeriod, 
																Z_SlowPeriod, 
																Z_BandsPeriod, 
																Z_Neighborhood, 
																Z_FibonacciOffset,
																Z_FibonacciMinDistance,
																Z_FibonacciQualifying,
																Z_FibonacciMinRetracement,
																Z_FibonacciMaxRetracement,
																Z_LevelQualifyingFlatAge,
																Z_LevelAge,
																Z_BrokenOnBodyTouch,
																TradesPerTrend,
																Z_SignalSplitBars);
				
				AddChartIndicator(RenkoKings_ZephyrusForce1);
			}
		}
		
		protected void CreateATM(OrderAction order_action)
		{
			m_atm_strategy_id = GetAtmStrategyUniqueId();
			m_order_id = GetAtmStrategyUniqueId();
			m_atm_order_pending = true;
			Print($"Creating ATM {order_action}");
			AtmStrategyCreate(order_action, OrderType.Market, 0, 0, TimeInForce.Day, m_order_id, AtmStrategy, m_atm_strategy_id, (atmCallbackErrorCode, atmCallBackId) => {
				//check that the atm strategy create did not result in error, and that the requested atm strategy matches the id in callback
				if (atmCallBackId == m_atm_strategy_id)
				{
					if (atmCallbackErrorCode == ErrorCode.NoError)
					{
						m_is_atm_created = true;
						m_atm_order_pending = false;
						m_order_action = order_action;
						m_trades_in_direction++;
						m_trade_bar = CurrentBar;
						Print($"ATM Order Created.  {m_trades_in_direction} / {TradesPerTrend}");
					}
					else
					{
						Print($"Failed to create ATM order");
						CloseStrategy(string.Empty);
					}
				}
			});
		}
		
		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) 
				return;

			if (CurrentBars[0] < 1)
				return;

			if (State != State.Realtime)
				return;
			
			if(m_trend_direction == 0) 
			{
				m_trend_direction = RenkoKings_ZephyrusForce1.Signal_Trend[0];
			}				
			
			if(m_trend_direction != RenkoKings_ZephyrusForce1.Signal_Trend[0])
			{
				m_trades_in_direction = 0;
				m_trend_direction = RenkoKings_ZephyrusForce1.Signal_Trend[0];
			}
			
			if (m_atm_order_pending)
				return;

			if(m_is_atm_created)
			{
				MarketPosition position = GetAtmStrategyMarketPosition(m_atm_strategy_id);
				string[] status = GetAtmStrategyEntryOrderStatus(m_order_id);
				
				//Print($"Market Position: {position}");
				if(status.Length != 3)
				{
					Print("Failed to get ATM Order status");
					CloseStrategy(string.Empty);
				}
				else if (position == MarketPosition.Flat && status[2] == "Filled")
				{
					Print($"Position Closed: {status[2]}");
					m_atm_strategy_id = string.Empty;
					m_order_id = string.Empty;
					m_is_atm_created  = false;
				}
			}
			
			else if (m_trades_in_direction >= TradesPerTrend)
			{
				return;
			}
			else if (m_trade_bar == CurrentBar)
			{
				return;
			}
			else if (TradeTrend && RenkoKings_ZephyrusForce1.Signal_Trade[0] == 1)
			{
				if(TakeProfit == 0 || StopLoss == 0 && AtmStrategy != "None")
					CreateATM(OrderAction.Buy);
				else
					EnterLong("GoLong");
			}
			else if (TradeFIB && RenkoKings_ZephyrusForce1.Signal_Trade[0] == 2)
			{
				if(TakeProfit == 0 || StopLoss == 0 && AtmStrategy != "None")
					CreateATM(OrderAction.Buy);
				else
					EnterLong("GoLong");
			}
			else if (TradePA && RenkoKings_ZephyrusForce1.Signal_Trade[0] == 3)
			{
				if(TakeProfit == 0 || StopLoss == 0 && AtmStrategy != "None")
					CreateATM(OrderAction.Buy);
				else
					EnterLong("GoLong");
			}
			else if (TradeTrend && RenkoKings_ZephyrusForce1.Signal_Trade[0] == -1)
			{
				if(TakeProfit == 0 || StopLoss == 0 && AtmStrategy != "None")
					CreateATM(OrderAction.SellShort);
				else
					EnterShort("GoShort");
			}
			else if (TradeFIB && RenkoKings_ZephyrusForce1.Signal_Trade[0] == -2)
			{
				if(TakeProfit == 0 || StopLoss == 0 && AtmStrategy != "None")
					CreateATM(OrderAction.SellShort);
				else
					EnterShort("GoShort");
			}
			else if (TradePA && RenkoKings_ZephyrusForce1.Signal_Trade[0] == -3)
			{
				if(TakeProfit == 0 || StopLoss == 0 && AtmStrategy != "None")
					CreateATM(OrderAction.SellShort);
				else
					EnterShort("GoShort");
			}
		}
		
		private void SetAlgoState(AlgoState state)
		{
			if(m_state == state)
				return;
			
			Print($"Old {m_state} New {state}");
			
			m_state = state;
		}
		

		public class FriendlyAtmConverter : TypeConverter
		{  
		    // Set the values to appear in the combo box
		    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
		    {
		        List<string> values = new List<string>();
				values.Add("None");
		        string[] files = System.IO.Directory.GetFiles(System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy"), "*.xml");  
		
		        foreach(string atm in files)
		        {
		            values.Add(System.IO.Path.GetFileNameWithoutExtension(atm));
		            NinjaTrader.Code.Output.Process(System.IO.Path.GetFileNameWithoutExtension(atm), PrintTo.OutputTab2);
		        }
		        return new StandardValuesCollection(values);
		    }
		
		    public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
		    {
		        return value.ToString();
		    }
		
		    public override object ConvertTo(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value, Type destinationType)
		    {
		        return value;
		    }
		
		    // required interface members needed to compile
		    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
		    { return true; }
		
		    public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
		    { return true; }
		
		    public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
		    { return true; }
		
		    public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
		    { return true; }
		}
		
	
		#region Properties
		[TypeConverter(typeof(FriendlyAtmConverter))] // Converts the found ATM template file names to string values
		[PropertyEditor("NinjaTrader.Gui.Tools.StringStandardValuesEditorKey")] // Create the combo box on the property grid
		[Display(Name = "Atm Strategy", Order = 1, GroupName = "ATM Strategy")]
		public string AtmStrategy
		{ get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name="Take Profit (0 == disabled)", Order=4, GroupName="TP / SL")]
		public double TakeProfit
		{ get; set; }		

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name="Stop Loss (0 == disabled)", Order=4, GroupName="TP / SL")]
		public double StopLoss
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Display(Name="Trade FIB Signals", Order=1, GroupName="Signals")]
		public bool TradeFIB
		{ get; set; }

		[Display(Name="Trade PA Signals", Order=2, GroupName="Signals")]
		public bool TradePA
		{ get; set; }

		[Display(Name="Trade Trend Signals", Order=3, GroupName="Signals")]
		public bool TradeTrend
		{ get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Number of Trades per Trend", Order=4, GroupName="Signals")]
		public int TradesPerTrend
		{ get; set; }		
		
		#endregion

		#region Zepherus Force Parameters
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Fast Period", Order=1, GroupName="Zepherus Force")]
		public int Z_FastPeriod
		{ get; set; }		

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Slow Period", Order=2, GroupName="Zepherus Force")]
		public int Z_SlowPeriod
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Bands Period", Order=3, GroupName="Zepherus Force")]
		public int Z_BandsPeriod
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Swing Point: Neighborhood", Order=4, GroupName="Zepherus Force")]
		public int Z_Neighborhood
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Fibonacci: Offset Ticks", Order=5, GroupName="Zepherus Force")]
		public int Z_FibonacciOffset
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Fibonacci: Min Distance (Bars)", Order=6, GroupName="Zepherus Force")]
		public int Z_FibonacciMinDistance
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Fibonacci: Qualifying (Bars)", Order=7, GroupName="Zepherus Force")]
		public int Z_FibonacciQualifying
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name="Fibonacci: Min Retracement (%)", Order=8, GroupName="Zepherus Force")]
		public double Z_FibonacciMinRetracement
		{ get; set; }		

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name="Fibonacci: Max Retracement (%)", Order=9, GroupName="Zepherus Force")]
		public double Z_FibonacciMaxRetracement
		{ get; set; }		

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Level: Qualifying Flat Age (Bars)", Order=10, GroupName="Zepherus Force")]
		public int Z_LevelQualifyingFlatAge
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Level: Age (Bars)", Order=11, GroupName="Zepherus Force")]
		public int Z_LevelAge
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Display(Name="Level: Broken On Body Touch", Order=12, GroupName="Zepherus Force")]
		public bool Z_BrokenOnBodyTouch
		{ get; set; }		
		
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name="Signal: Split (Bars)", Order=13, GroupName="Zepherus Force")]
		public int Z_SignalSplitBars
		{ get; set; }		
		
		
		
		// ,, int signalPAQuantityPerTrend, int signalSplitBars
		
		#endregion
	}
	
	
}
