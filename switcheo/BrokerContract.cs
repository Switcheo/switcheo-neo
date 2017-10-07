using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using System;
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

        [Appcall("AddRPXScriptHash")] // TODO
        public static extern object CallRPXContract(string method, byte[] args);

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

            switch (operation)
            {
                case "queryOffers":
                    return new byte[] { };
                case "queryOfferDetails":
                    return new byte[] { };
                case "makeOffer":
                    return MakeOffer((byte[])args[0], (byte[])args[1], (AssetCategory)args[2], (BigInteger)args[3], (byte[])args[4], (AssetCategory)args[5], (BigInteger)args[6], (byte[])args[7], (byte[])args[8]);
                case "fillOffer":
                    return false;
                case "cancelOffer":
                    return false;
                case "withdrawAssets":
                    return false;
                case "freezeContract":
                    return false;
                default:
                    return false;
            }
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

            // Take fee?

            TransactionOutput output = new TransactionOutput(); // TODO: how to get the current transaction?
            if (offerAssetCategory == AssetCategory.SystemAsset)
            {
                // Check the input transaction for the assets
                if (output.AssetId != offerAssetID || output.Value != offerAmount) return false;

            }
            else
            {
                // Check that no assets were sent by mistake
                if (output.Value > 0) return false;

                // Check allowance

                // Transfer token

            }

            // Store the offer maker address and filled amount under the offer hash
            Storage.Put(Storage.CurrentContext, offerHash, makerAddress.Concat(IntToBytes(0)));

            return true;
        }

        private static bool FillOffer(byte[] offerHash, byte[] offerAddress, string amountToFill) // TODO: we can't just send in the offer hash - full offer details are required for doing the token swap
        {
            // Check that the filler is different from the maker

            // Check that the offer exists and 0 < amount to fill <= available amount

            // Check that the required amounts are sent

            // Move asset to the maker holding 

            // Move asset to the taker holding

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

            // Check that there are asset value > 0

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
