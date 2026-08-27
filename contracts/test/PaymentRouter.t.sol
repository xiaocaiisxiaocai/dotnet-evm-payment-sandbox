// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {IERC20Errors} from "@openzeppelin/contracts/interfaces/draft-IERC6093.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {Vm} from "forge-std/Vm.sol";

import {PaymentRouter} from "../src/PaymentRouter.sol";
import {FalseReturnToken} from "./fixtures/FalseReturnToken.sol";
import {NoReturnToken} from "./fixtures/NoReturnToken.sol";
import {PaymentRouterTestBase} from "./helpers/PaymentRouterTestBase.sol";

contract PaymentRouterTest is PaymentRouterTestBase {
    function test_payTransfersAtomicUnitsAndEmitsCompleteEvent() public {
        uint256 amount = 1_234_567; // 1.234567 tUSDC, represented without floating point.

        vm.prank(payer);
        usdc.approve(address(router), amount);

        vm.expectEmit(true, true, true, true, address(router));
        emit PaymentRecorded(PAYMENT_ID, payer, address(usdc), merchant, amount);

        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);

        assertEq(usdc.balanceOf(payer), INITIAL_USDC_BALANCE - amount);
        assertEq(usdc.balanceOf(merchant), amount);
        assertEq(usdc.balanceOf(address(router)), 0, "the Router must not custody payment funds");
    }

    function test_paySupportsSixAndEighteenDecimalTokensWithoutConversion() public {
        uint256 oneUsdc = 1_000_000;
        uint256 oneToken18 = 10 ** uint256(token18.decimals());

        _approveAndPay(usdc, PAYMENT_ID, oneUsdc);

        vm.startPrank(payer);
        token18.approve(address(router), oneToken18);
        router.pay(keccak256("payment-intent-002"), address(token18), merchant, oneToken18);
        vm.stopPrank();

        assertEq(usdc.balanceOf(merchant), oneUsdc);
        assertEq(token18.balanceOf(merchant), oneToken18);
        assertEq(token18.balanceOf(address(router)), 0);
    }

    function test_payAllowsPartialPaymentsWithTheSamePaymentId() public {
        uint256 firstPart = 2_000_000;
        uint256 secondPart = 3_000_000;

        vm.startPrank(payer);
        usdc.approve(address(router), firstPart + secondPart);

        vm.expectEmit(true, true, true, true, address(router));
        emit PaymentRecorded(PAYMENT_ID, payer, address(usdc), merchant, firstPart);
        router.pay(PAYMENT_ID, address(usdc), merchant, firstPart);

        vm.expectEmit(true, true, true, true, address(router));
        emit PaymentRecorded(PAYMENT_ID, payer, address(usdc), merchant, secondPart);
        router.pay(PAYMENT_ID, address(usdc), merchant, secondPart);
        vm.stopPrank();

        assertEq(usdc.balanceOf(merchant), firstPart + secondPart);
    }

    function test_payAllowsAnExactRepeatedPaymentIdAndAmount() public {
        uint256 amount = 1e6;

        vm.startPrank(payer);
        usdc.approve(address(router), amount * 2);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);
        vm.stopPrank();

        assertEq(usdc.balanceOf(merchant), amount * 2);
    }

    function test_payRevertsWithoutAllowance() public {
        uint256 amount = 1e6;

        vm.expectRevert(
            abi.encodeWithSelector(IERC20Errors.ERC20InsufficientAllowance.selector, address(router), 0, amount)
        );
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);
    }

    function test_payRevertsWhenBalanceIsInsufficient() public {
        uint256 amount = INITIAL_USDC_BALANCE + 1;

        vm.prank(payer);
        usdc.approve(address(router), amount);

        vm.expectRevert(
            abi.encodeWithSelector(IERC20Errors.ERC20InsufficientBalance.selector, payer, INITIAL_USDC_BALANCE, amount)
        );
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);
    }

    function test_failedPaymentLeavesNoRecordedLogs() public {
        uint256 amount = INITIAL_USDC_BALANCE + 1;

        // Approval is deliberately outside the recording window. Any captured
        // log would therefore have to come from the failing payment call.
        vm.prank(payer);
        usdc.approve(address(router), amount);
        vm.recordLogs();

        vm.expectRevert(
            abi.encodeWithSelector(IERC20Errors.ERC20InsufficientBalance.selector, payer, INITIAL_USDC_BALANCE, amount)
        );
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), merchant, amount);

        Vm.Log[] memory recordedLogs = vm.getRecordedLogs();
        assertEq(recordedLogs.length, 0, "a reverted payment must not leave PaymentRecorded or token logs");
    }

    function test_payRevertsForZeroPaymentId() public {
        vm.expectRevert(PaymentRouter.InvalidPaymentId.selector);
        vm.prank(payer);
        router.pay(bytes32(0), address(usdc), merchant, 1);
    }

    function test_payRevertsForZeroToken() public {
        vm.expectRevert(abi.encodeWithSelector(PaymentRouter.InvalidToken.selector, address(0)));
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(0), merchant, 1);
    }

    function test_payRevertsForNonContractToken() public {
        address notAToken = makeAddr("not-a-token");

        vm.expectRevert(abi.encodeWithSelector(PaymentRouter.InvalidToken.selector, notAToken));
        vm.prank(payer);
        router.pay(PAYMENT_ID, notAToken, merchant, 1);
    }

    function test_payRevertsForZeroMerchant() public {
        vm.expectRevert(abi.encodeWithSelector(PaymentRouter.InvalidMerchant.selector, address(0)));
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), address(0), 1);
    }

    function test_payRevertsWhenMerchantIsRouter() public {
        vm.expectRevert(abi.encodeWithSelector(PaymentRouter.InvalidMerchant.selector, address(router)));
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), address(router), 1);
    }

    function test_payRevertsForZeroAmount() public {
        vm.expectRevert(PaymentRouter.InvalidAmount.selector);
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(usdc), merchant, 0);
    }

    function test_payRevertsWhenTokenReturnsFalse() public {
        FalseReturnToken falseToken = new FalseReturnToken();

        vm.expectRevert(abi.encodeWithSelector(SafeERC20.SafeERC20FailedOperation.selector, address(falseToken)));
        vm.prank(payer);
        router.pay(PAYMENT_ID, address(falseToken), merchant, 1);
    }

    function test_paySupportsAValidNoReturnToken() public {
        NoReturnToken noReturnToken = new NoReturnToken();
        uint256 amount = 500;
        noReturnToken.mint(payer, amount);

        vm.prank(payer);
        noReturnToken.approve(address(router), amount);

        vm.prank(payer);
        router.pay(PAYMENT_ID, address(noReturnToken), merchant, amount);

        assertEq(noReturnToken.balanceOf(payer), 0);
        assertEq(noReturnToken.balanceOf(merchant), amount);
        assertEq(noReturnToken.balanceOf(address(router)), 0);
    }
}
