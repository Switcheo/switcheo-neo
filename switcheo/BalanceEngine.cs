
using Neo.SmartContract.Framework;
using Neo.VM;
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
        [OpCode(OpCode.APPEND)]
        public extern static void Append(BalanceChange[] array, BalanceChange item);

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
            BalanceChange balanceChange = new BalanceChange
            {
                AssetID = assetID,
                Amount = amount,
                ReasonCode = reason
            };
            if (balanceChanges.HasKey(address))
            {
                Append(balanceChanges[address], balanceChange);
            }
            else
            {
                // create new array if its a new address
                balanceChanges[address] = new BalanceChange[] { balanceChange };
            }
            return balanceChanges;
        }
    }
}