/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using NUnit.Framework;
using QuantConnect.Brokerages.GMOCoin.Api;

namespace QuantConnect.Brokerages.GMOCoin.Tests
{
    [TestFixture]
    public class GMOCoinAuthenticatorTests
    {
        [Test]
        public void SignatureMatchesKnownVector()
        {
            // message = timestamp + method + path (starting with /v1, no query string) + body
            // vector independently computed with Python hmac/hashlib
            var signature = GMOCoinAuthenticator.Sign("hoge", "1721121776490GET/v1/account/assets");
            Assert.AreEqual("daff080eff4acc3aca91b9cc2ba84c6c1f302a2c7625683c8d80345d988d724d", signature);
        }

        [Test]
        public void PostSignatureSignsRawBody()
        {
            const string body = "{\"symbol\":\"BTC\",\"side\":\"BUY\",\"executionType\":\"LIMIT\",\"timeInForce\":\"FAS\",\"price\":\"1234000\",\"size\":\"0.0001\"}";
            var signature = GMOCoinAuthenticator.Sign("hoge", "1721121776490POST/v1/order" + body);
            Assert.AreEqual("5a3fc2c0f55d487fd6e53b0bac35cf6a22ab1c7077763405a4875d351fd0b0f8", signature);
        }

        [Test]
        public void GetHeadersProducesTimestampMethodPathSignature()
        {
            var authenticator = new GMOCoinAuthenticator("my-key", "hoge");
            var headers = authenticator.GetHeaders("GET", "/v1/account/assets", "", 1721121776490);

            Assert.AreEqual("my-key", headers["API-KEY"]);
            Assert.AreEqual("1721121776490", headers["API-TIMESTAMP"]);
            Assert.AreEqual(
                GMOCoinAuthenticator.Sign("hoge", "1721121776490GET/v1/account/assets"),
                headers["API-SIGN"]);
        }

        [Test]
        public void PostHeadersIncludeBodyInSignature()
        {
            var authenticator = new GMOCoinAuthenticator("my-key", "hoge");
            const string body = "{\"orderId\":123}";
            var headers = authenticator.GetHeaders("POST", "/v1/cancelOrder", body, 1721121776490);

            Assert.AreEqual(
                GMOCoinAuthenticator.Sign("hoge", "1721121776490POST/v1/cancelOrder" + body),
                headers["API-SIGN"]);
        }

        [Test]
        public void HasCredentialsReflectsInput()
        {
            Assert.IsTrue(new GMOCoinAuthenticator("k", "s").HasCredentials);
            Assert.IsFalse(new GMOCoinAuthenticator("", "").HasCredentials);
            Assert.IsFalse(new GMOCoinAuthenticator(null, null).HasCredentials);
        }
    }
}
