// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {TestToken18} from "../src/testtokens/TestToken18.sol";
import {PaymentRouterTestBase} from "./helpers/PaymentRouterTestBase.sol";

contract PaymentRouterFuzzTest is PaymentRouterTestBase {
    function testFuzz_payTransfersExactUsdcAmount(uint256 rawAmount) public {
        uint256 amount = bound(rawAmount, 1, INITIAL_USDC_BALANCE);

        vm.startPrank(payer);
        usdc.approve(address(router), amount);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);
        vm.stopPrank();

        assertEq(usdc.balanceOf(payer), INITIAL_USDC_BALANCE - amount);
        assertEq(usdc.balanceOf(merchant), amount);
        assertEq(usdc.balanceOf(address(router)), 0);
    }

    function testFuzz_payTransfersExactEighteenDecimalAmount(uint256 rawAmount) public {
        uint256 amount = bound(rawAmount, 1, INITIAL_TOKEN18_BALANCE);

        vm.startPrank(payer);
        token18.approve(address(router), amount);
        router.pay(PAYMENT_ID, address(token18), merchant, amount);
        vm.stopPrank();

        assertEq(token18.balanceOf(payer), INITIAL_TOKEN18_BALANCE - amount);
        assertEq(token18.balanceOf(merchant), amount);
        assertEq(token18.balanceOf(address(router)), 0);
    }

    function test_paySupportsMaximumUint256ForAStandardToken() public {
        TestToken18 maximumToken = new TestToken18();
        maximumToken.mint(payer, type(uint256).max);

        vm.startPrank(payer);
        maximumToken.approve(address(router), type(uint256).max);
        router.pay(PAYMENT_ID, address(maximumToken), merchant, type(uint256).max);
        vm.stopPrank();

        assertEq(maximumToken.balanceOf(payer), 0);
        assertEq(maximumToken.balanceOf(merchant), type(uint256).max);
        assertEq(maximumToken.balanceOf(address(router)), 0);
    }
}
