using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace switcheo
{
    public class BrokerContract : SmartContract
    {
        public enum AssetCategory : byte
        {
            SystemAsset = 0x00,
            SmartContract = 0x01
        }

        [Appcall("1c4f43f942b56ed906dba00b7f3c7ce3da3dd11077532baed900c2cc8c7f247e")] // TODO: Add RPX ScriptHash
        public static extern object CallRPXContract(string method, params object[] args);

        /// <summary>
        ///   This is the Switcheo smart contract entrypoint.
        /// 
        ///   Parameter List: 0705
        ///   Return List: 05
        /// </summary>
        /// <param name="method">
        ///   The method to be invoked.
        /// </param>
        /// <param name="args">
        ///   Input parameters for the delegated method.
        /// </param>
        public static object Main(string operation, params object[] args)
        {
            if (operation == "queryOffers")
                return new byte[] { };
            if (operation == "queryOfferDetails")
                return new byte[] { };
            if (operation == "makeOffer")
                return MakeOffer((byte[])args[0], (byte[])args[1], (AssetCategory)args[2], (BigInteger)args[3], (byte[])args[4], (AssetCategory)args[5], (BigInteger)args[6], (byte[])args[7], (byte[])args[8]);
            if (operation == "fillOffer")
                return false;
            if (operation == "cancelOffer")
                return false;
            if (operation == "withdrawAssets")
                return false;
            if (operation == "freezeContract")
                return false;

            return false;
        }

        private static byte[] QueryOffers(byte[] offerAssetID, byte[] offerAssetCategory, byte[] wantAssetID, byte[] wantAssetCategory)
        {
            return Storage.Get(Storage.CurrentContext, offerAssetID.Concat(offerAssetCategory).Concat(wantAssetID).Concat(wantAssetCategory));
        }

        private static bool MakeOffer(
            byte[] makerAddress,
            byte[] offerAssetID, AssetCategory offerAssetCategory, BigInteger offerAmount, 
            byte[] wantAssetID, AssetCategory wantAssetCategory, BigInteger wantAmount, 
            byte[] nounce, byte[] signature
            )
        {
            // Check that the maker is honest
            if (!Runtime.CheckWitness(makerAddress)) return false;

            // Check signature of maker
            if (!VerifySignature(signature, makerAddress)) return false;

            // Check that the amounts > 0
            if (offerAmount <= 0 || wantAmount <= 0) return false;

            // Check the trade is across different assets
            if (offerAssetID == wantAssetID && offerAssetCategory == wantAssetCategory) return false;

            // Check that nonce is not repeated
            byte[] offerHash = Hash256(
                makerAddress
                .Concat(offerAssetID)
                .Concat(new byte[] { (byte)offerAssetCategory })
                .Concat(IntToBytes(offerAmount))
                .Concat(wantAssetID)
                .Concat(IntToBytes(wantAmount))
                .Concat(new byte[] { (byte)wantAssetCategory })
                .Concat(nounce));
            if (Storage.Get(Storage.CurrentContext, offerHash).Length != 0) return false;

            // Get current transaction
            var currentTxn = (Transaction) ExecutionEngine.ScriptContainer;
            var outputs = currentTxn.GetOutputs();
            
            // Verify that the offer really has the indicated assets available
            if (offerAssetCategory == AssetCategory.SystemAsset)
            {
                // Check the current transaction for the system assets
                TransactionOutput requiredAsset = null;
                foreach (var o in outputs)
                {
                    if (o.AssetId == offerAssetID && o.Value == offerAmount && o.ScriptHash == ExecutionEngine.ExecutingScriptHash)
                    {
                        requiredAsset = o;
                        break;
                    }
                }
                if (requiredAsset == null) return false;
            }
            else if (offerAssetCategory == AssetCategory.SmartContract)
            {
                // Check that no assets were sent by mistake
                if (outputs.Length > 0) return false;

                // Check allowance on smart contract
                BigInteger allowedAmount = (BigInteger) CallRPXContract("allowance", makerAddress, ExecutionEngine.ExecutingScriptHash);
                if (allowedAmount < offerAmount) return false;

                // Transfer token
                bool transferSuccessful = (bool) CallRPXContract("transferFrom", ExecutionEngine.ExecutingScriptHash, makerAddress, ExecutionEngine.ExecutingScriptHash);
                if (!transferSuccessful) return false;
            }
            else 
            {
                // Unknown asset category
                return false;
            }

            // Store the offer maker address and filled amount under the offer hash
            Storage.Put(Storage.CurrentContext, offerHash, makerAddress.Concat(IntToBytes(0)));

            return true;
        }

        private static bool FillOffer(
            byte[] offerHash, byte[] makerAddress,
            byte[] offerAssetID, AssetCategory offerAssetCategory,
            byte[] wantAssetID, AssetCategory wantAssetCategory,
            BigInteger amountToFill)
        {
            // Check that the filler is honest

            // Check signature of filler

            // Check that the filler is different from the maker

            // Check that the offer exists and 0 < amount to fill <= available amount

            // Check that the required amounts are sent

            // Check asset precisions to calculate who to take fees from and how much

            // Transfer fees

            // Move asset to the maker balance 

            // Move asset to the taker balance

            // Update filled amount

            // Remove order if completely filled

            return true;
        }

        private static bool CancelOffer(byte[] offerHash, byte[] cancellerAddress, byte[] signature)
        {
            // Check that the canceller is honest
            if (!Runtime.CheckWitness(cancellerAddress)) return false;

            // Check signature of canceller
            if (!VerifySignature(signature, cancellerAddress)) return false;

            // Check that the canceller is also the offer maker

            return true;
        }

        private static bool WithdrawAssets(byte[] holderAddress, byte[] assetID, byte[] AssetCategory, string withdrawToThisAddress)
        {
            // Check that the holder is honest

            // Check the signature of the holder

            // Check that there are asset value > 0 in balance

            // Transfer asset

            return true;
        }

        private static byte[] IntToBytes(BigInteger value)
        {
            byte[] buffer = value.ToByteArray();
            return buffer;
        }

        private static BigInteger BytesToInt(byte[] array)
        {
            var buffer = new BigInteger(array);
            return buffer;
        }
    }
}
