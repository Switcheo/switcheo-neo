
using Neo.SmartContract.Framework;
using System;
using System.Numerics;

namespace switcheo
{
  public struct BalanceChange
  {
    public byte[] AssetID;
    public BigInteger Amount;
    public byte[] ReasonCode;
  }
  public static class BalanceEngineHelpers
  {

    public static Map<byte[], BalanceChange[]> IncreaseBalance(this Map<byte[], BalanceChange[]> balanceChanges, byte[] address, byte[] assetID, BigInteger amount, byte[] reason)
    {
      return ChangeBalance(balanceChanges, address, assetID, amount, reason);
    }

    public static Map<byte[], BalanceChange[]> ReduceBalance(this Map<byte[], BalanceChange[]> balanceChanges, byte[] address, byte[] assetID, BigInteger amount, byte[] reason)
    {
      return ChangeBalance(balanceChanges, address, assetID, 0 - amount, reason);
    }

    private static Map<byte[], BalanceChange[]> ChangeBalance(this Map<byte[], BalanceChange[]> balanceChanges, byte[] address, byte[] assetID, BigInteger amount, byte[] reason)
    {
      if (amount < 1) throw new ArgumentOutOfRangeException();
      BalanceChange balanceChange = new BalanceChange
      {
        AssetID = assetID,
        Amount = 0 + amount,
        ReasonCode = reason
      };
      // create new list if its a new address
      if (!balanceChanges.HasKey(address))
      {
        balanceChanges[address] = new BalanceChange[] { balanceChange };
      }
      else
      {
        balanceChanges[address] = AddToArray(balanceChanges[address], balanceChange);
      }
      return balanceChanges;
    }

    private static T[] AddToArray<T>(this T[] target, params T[] items)
    {
      if (target == null)
      {
        target = new T[] { };
      }
      if (items == null)
      {
        items = new T[] { };
      }

      // Join the arrays
      T[] result = new T[target.Length + items.Length];
      target.CopyTo(result, 0);
      items.CopyTo(result, target.Length);
      return result;
    }
  }
}